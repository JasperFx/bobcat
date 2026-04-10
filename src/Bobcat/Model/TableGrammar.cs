using System.Reflection;
using Bobcat.Engine;

namespace Bobcat.Model;

/// <summary>
/// A grammar that maps table rows to repeated method invocations.
/// Each row in the Step's Rows becomes a separate execution step.
/// </summary>
public class TableGrammar : IGrammar
{
    private readonly MethodInfo _method;
    private readonly StepKind _stepKind;
    private readonly ParameterInfo[] _parameters;

    public TableGrammar(MethodInfo method, StepAttribute attribute, StepKind stepKind)
    {
        _method = method;
        _stepKind = stepKind;
        _parameters = method.GetParameters();
        Name = method.Name;
    }

    public string Name { get; }

    public void CreatePlan(ExecutionPlan plan, Step step, FixtureInstance fixture)
    {
        for (var i = 0; i < step.Rows.Count; i++)
        {
            plan.Add(new TableRowExecutionStep(this, step, fixture, i));
        }
    }

    private class TableRowExecutionStep : IExecutionStep
    {
        private readonly TableGrammar _grammar;
        private readonly Step _step;
        private readonly FixtureInstance _fixture;
        private readonly int _rowIndex;

        public TableRowExecutionStep(TableGrammar grammar, Step step, FixtureInstance fixture, int rowIndex)
        {
            _grammar = grammar;
            _step = step;
            _fixture = fixture;
            _rowIndex = rowIndex;
        }

        public string StepId => $"{_step.Id}.row{_rowIndex + 1}";
        public StepKind StepKind => _grammar._stepKind;

        public async Task Execute(IStepContext context, StepResult result, CancellationToken token)
        {
            var fixtureInstance = _fixture.Instance;
            fixtureInstance.Context = context;

            var row = _step.Rows[_rowIndex];
            var args = ResolveArguments(row);

            try
            {
                var returnValue = _grammar._method.Invoke(fixtureInstance, args);
                if (returnValue is Task task)
                {
                    await task;
                }

                if (result.StepStatus == ResultStatus.ok)
                {
                    result.MarkSuccess();
                }
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }

        private object?[] ResolveArguments(Dictionary<string, object> row)
        {
            var parameters = _grammar._parameters;
            var args = new object?[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                if (row.TryGetValue(param.Name!, out var value))
                {
                    args[i] = ConvertValue(value, param.ParameterType);
                }
                else if (param.HasDefaultValue)
                {
                    args[i] = param.DefaultValue;
                }
                else
                {
                    args[i] = param.ParameterType.IsValueType
                        ? Activator.CreateInstance(param.ParameterType)
                        : null;
                }
            }

            return args;
        }

        private static object? ConvertValue(object value, Type targetType)
        {
            if (value.GetType() == targetType) return value;
            if (value is string s) return Convert.ChangeType(s, targetType);
            return Convert.ChangeType(value, targetType);
        }
    }
}
