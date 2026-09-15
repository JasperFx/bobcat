using Bobcat.Console.Contracts;
using Bobcat.Console.Runs;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// Issue #304, the read side: a marker-comment scenario's declared narrative reaches
/// <c>GET /api/runs/{id}</c>, and each recorded step says which sentence it ran under.
/// </summary>
/// <remarks>
/// The projection deliberately does NOT fold the two together — a declared step has no verdict
/// and never gains one here, and the viewer is what puts the work under the sentence. Keeping the
/// join in the read model rather than in the fold is what lets a declared step with nothing under
/// it stay visibly blank instead of quietly rendering as a pass.
/// </remarks>
public class DeclaredNarrativeProjectionTests : IDisposable
{
    private static readonly Guid run = Guid.Parse("9a1f1a1e-0000-0000-0000-000000000304");

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-narrative-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private RunDetail Detail(MonitorRunRegistry registry)
        => RunEndpoints.Find(run, registry).ShouldBeOfType<Ok<RunDetail>>().Value.ShouldNotBeNull();

    [Fact]
    public void the_narrative_and_its_attributions_reach_the_run_detail()
    {
        var t0 = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        using var registry = new MonitorRunRegistry(_dataPath);
        registry.Record(
        [
            new RunStarted(run, "Daemon", "/repo", "main", "in-process", t0, 2),
            new ScenarioStarted(run, "Async daemon/catches up", "Async daemon", "catches up", 1, t0,
                TotalSteps: 3,
                DeclaredSteps:
                [
                    new DeclaredStepInfo("Given", "the events are published"),
                    new DeclaredStepInfo("When", "the daemon is running"),
                    new DeclaredStepInfo("Then", "every aggregate matches")
                ]),
            new StepStarted(run, "Async daemon/catches up", "s1", "Given", "the events are published",
                StepNumber: 1, TotalSteps: 3, ScenarioElapsedMs: 0, DeclaredStepNumber: 1),
            new StepFinished(run, "Async daemon/catches up", "s1", "Passed", 40, null),
            // The third sentence has no decorated helper under it — nothing observed it, and the
            // read model must not invent anything for it.
            new StepStarted(run, "Async daemon/catches up", "s2", "When", "the daemon is running",
                StepNumber: 2, TotalSteps: 3, ScenarioElapsedMs: 40, DeclaredStepNumber: 2),
            new StepFinished(run, "Async daemon/catches up", "s2", "Passed", 12, null),
            new ScenarioFinished(run, "Async daemon/catches up", "CleanPass", 1, 60, null, At: t0.AddSeconds(1)),

            // A scenario from every other authoring style: steps, no narrative at all.
            new ScenarioStarted(run, "Wallets/credit", "Wallets", "credit", 1, t0),
            new StepStarted(run, "Wallets/credit", "s1", "Given", "a wallet"),
            new StepFinished(run, "Wallets/credit", "s1", "Passed", 5, null),
            new ScenarioFinished(run, "Wallets/credit", "CleanPass", 1, 10, null, At: t0.AddSeconds(1)),
            new RunFinished(run, 0, 2, 0, 0, 0, t0.AddSeconds(2))
        ]);

        var detail = Detail(registry);

        var daemon = detail.Scenarios.Single(s => s.Uid == "Async daemon/catches up");
        daemon.DeclaredSteps.Select(x => $"{x.Keyword} {x.Text}").ShouldBe(
        [
            "Given the events are published",
            "When the daemon is running",
            "Then every aggregate matches"
        ]);
        daemon.Steps.Select(x => x.DeclaredStepNumber).ShouldBe([1, 2]);

        var wallets = detail.Scenarios.Single(s => s.Uid == "Wallets/credit");
        wallets.DeclaredSteps.ShouldBeEmpty();
        wallets.Steps.ShouldHaveSingleItem().DeclaredStepNumber.ShouldBeNull();
    }

    [Fact]
    public void a_retry_re_announces_the_narrative_rather_than_losing_it()
    {
        // Every attempt gets a fresh reset/begin/end bracket and the step list starts over. The
        // narrative is a property of the TEST, not of the attempt, so it has to survive that —
        // and it does by being re-announced, which is also what makes hydration idempotent.
        var t0 = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        using var registry = new MonitorRunRegistry(_dataPath);

        DeclaredStepInfo[] narrative =
        [
            new DeclaredStepInfo("Given", "the events are published"),
            new DeclaredStepInfo("Then", "every aggregate matches")
        ];

        registry.Record(
        [
            new RunStarted(run, "Daemon", "/repo", "main", "in-process", t0, 1),
            new ScenarioStarted(run, "Async daemon/catches up", "Async daemon", "catches up", 1, t0,
                TotalSteps: 2, DeclaredSteps: narrative),
            new StepStarted(run, "Async daemon/catches up", "s1", "Given", "the events are published",
                DeclaredStepNumber: 1),
            new StepFinished(run, "Async daemon/catches up", "s1", "Failed", 9, "no shard"),
            new RetryScheduled(run, "Async daemon/catches up", 2, "Retry", "flaky"),
            new ScenarioStarted(run, "Async daemon/catches up", "Async daemon", "catches up", 2, t0.AddSeconds(1),
                TotalSteps: 2, DeclaredSteps: narrative),
            new StepStarted(run, "Async daemon/catches up", "s1", "Given", "the events are published",
                DeclaredStepNumber: 1),
            new StepFinished(run, "Async daemon/catches up", "s1", "Passed", 11, null),
            new ScenarioFinished(run, "Async daemon/catches up", "PassOnRetry", 2, 30, null, At: t0.AddSeconds(2)),
            new RunFinished(run, 0, 1, 0, 1, 0, t0.AddSeconds(3))
        ]);

        var scenario = Detail(registry).Scenarios.Single();
        scenario.DeclaredSteps.Length.ShouldBe(2);
        scenario.Steps.ShouldHaveSingleItem().DeclaredStepNumber.ShouldBe(1);
    }
}
