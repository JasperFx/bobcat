using System.Reflection;
using Bobcat.Engine;

namespace Bobcat.Model;

/// <summary>
/// A grammar that wraps a method returning bool — true = pass, false = fail.
/// Always classified as StepKind.Then.
/// </summary>
public class Fact : IGrammar
{
    private readonly MethodInfo _method;
    private readonly string _expression;

    public Fact(MethodInfo method, CheckAttribute attribute)
    {
        _method = method;
        _expression = attribute.Expression;
        Name = method.Name;
    }

    public string Name { get; }

    public void CreatePlan(ExecutionPlan plan, Step step, FixtureInstance fixture)
    {
        plan.Add(new FactExecutionStep(this, step, fixture));
    }

    internal string Expression => _expression;

    private class FactExecutionStep : IExecutionStep
    {
        private readonly Fact _fact;
        private readonly Step _step;
        private readonly FixtureInstance _fixture;

        public FactExecutionStep(Fact fact, Step step, FixtureInstance fixture)
        {
            _fact = fact;
            _step = step;
            _fixture = fixture;
        }

        public string StepId => _step.Id;
        public StepKind StepKind => StepKind.Then;

        public async Task Execute(IStepContext context, StepResult result, CancellationToken token)
        {
            var fixtureInstance = _fixture.Instance;
            fixtureInstance.Context = context;

            try
            {
                var returnValue = _fact._method.Invoke(fixtureInstance, []);
                bool passed;

                if (returnValue is Task<bool> asyncResult)
                {
                    passed = await asyncResult;
                }
                else if (returnValue is bool syncResult)
                {
                    passed = syncResult;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"[Fact] method '{_fact._method.Name}' must return bool or Task<bool>");
                }

                if (passed)
                {
                    result.MarkSuccess();
                }
                else
                {
                    result.MarkFailed();
                }
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }
    }
}
