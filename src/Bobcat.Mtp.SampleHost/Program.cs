using Bobcat;
using Bobcat.Engine;
using Bobcat.Mtp;
using Bobcat.Runtime;

namespace Bobcat.Mtp.SampleHost;

/// <summary>
/// A Bobcat spec project running as an MTP test host. Features are hand-built rather than
/// generated so the outcomes are exact and the end-to-end tests can assert on them.
/// </summary>
public static class Program
{
    public static Task<int> Main(string[] args)
        => BobcatTestApplication.Run(args, runner =>
        {
            runner.AddFeature(Arithmetic());
            runner.AddFeature(Inventory());
        });

    public class SampleFixture : Fixture;

    private static FeatureDefinition Arithmetic() => new(
        "Arithmetic", typeof(SampleFixture),
        [
            Scenario("addition works", [], _ => { }),
            Scenario("subtraction disagrees", [],
                result => result.MarkCells(new CellResult("result", ResultStatus.failed)
                {
                    Expected = "4", Actual = "5"
                })),
            Scenario("division explodes", [],
                _ => throw new InvalidOperationException("attempted to divide by zero"))
        ]);

    private static FeatureDefinition Inventory() => new(
        "Inventory", typeof(SampleFixture),
        [
            Scenario("stock is counted", ["regression"], _ => { }),
            Scenario("restock is flaky", ["isolated", "recycle(rabbit)"], _ => { })
        ]);

    private static ScenarioDefinition Scenario(string title, string[] tags, Action<StepResult> body)
        => new(title, tags, (_, plan) =>
            plan.Add(new DelegateExecutionStep("step-1", StepKind.Then, title, (_, result, _) =>
            {
                body(result);
                return Task.CompletedTask;
            })));
}
