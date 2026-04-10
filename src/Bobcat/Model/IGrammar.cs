using Bobcat.Engine;

namespace Bobcat.Model;

public interface IGrammar
{
    string Name { get; }
    void CreatePlan(ExecutionPlan plan, Step step, FixtureInstance fixture);
}
