using System.Reflection;
using Bobcat.Engine;
using Bobcat.Rendering;

namespace Bobcat.Model;

/// <summary>
/// A grammar that wraps a single attributed method on a fixture.
/// This is the fundamental grammar type — one method call, with parameters
/// filled from the Step's values.
/// </summary>
public class Sentence : IGrammar
{
    private readonly MethodInfo _method;
    private readonly StepKind _stepKind;
    private readonly string _expression;
    private readonly ParameterInfo[] _parameters;

    public Sentence(MethodInfo method, StepAttribute attribute, StepKind stepKind)
    {
        _method = method;
        _stepKind = stepKind;
        _expression = attribute.Expression;
        _parameters = method.GetParameters();
        Name = method.Name;
    }

    public string Name { get; }

    public void CreatePlan(ExecutionPlan plan, Step step, FixtureInstance fixture)
    {
        plan.Add(new SentenceExecutionStep(this, step, fixture));
    }

    internal StepKind StepKind => _stepKind;
    internal MethodInfo Method => _method;
    internal string Expression => _expression;
    internal ParameterInfo[] Parameters => _parameters;

    private class SentenceExecutionStep : IExecutionStep
    {
        private readonly Sentence _sentence;
        private readonly Step _step;
        private readonly FixtureInstance _fixture;

        public SentenceExecutionStep(Sentence sentence, Step step, FixtureInstance fixture)
        {
            _sentence = sentence;
            _step = step;
            _fixture = fixture;
        }

        public string StepId => _step.Id;
        public StepKind StepKind => _sentence._stepKind;

        public async Task Execute(IStepContext context, StepResult result, CancellationToken token)
        {
            var fixtureInstance = _fixture.Instance;
            fixtureInstance.Context = context;

            var args = ResolveArguments(_step);

            try
            {
                var returnValue = _sentence._method.Invoke(fixtureInstance, args);
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

        private object?[] ResolveArguments(Step step)
        {
            var parameters = _sentence._parameters;
            var args = new object?[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                if (step.TryGetValue(param.Name!, out var value))
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
