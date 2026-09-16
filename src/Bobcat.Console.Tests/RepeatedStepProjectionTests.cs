using Bobcat.Console.Contracts;
using Bobcat.Console.Runs;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// Issue #322: a step repeated within one scenario reaches a verdict of its own.
/// </summary>
/// <remarks>
/// <c>StepId</c> is a step TEMPLATE id — the step method's name — not an identity, and the two
/// step shapes the grammar most recently encouraged both repeat: <c>Given {event} occurred</c>
/// once per arranged event (#259) and <c>And no events for {aggregate} "…"</c> once per re-pointed
/// stream (#311, #320). Resolving a <c>StepFinished</c> by StepId alone handed every finish to the
/// FIRST occurrence, so every later one sat at "running" forever — inside a scenario the runner
/// had already passed. Measured on a real suite: 33 of 182 steps across 21 of 37 scenarios, every
/// one of those scenarios a CleanPass.
/// </remarks>
public class RepeatedStepProjectionTests : IDisposable
{
    private static readonly Guid run = Guid.Parse("9a1f1a1e-0000-0000-0000-000000000322");
    private const string Uid = "AppointmentsQueue/The queue counts appointments from every stream in the shelter";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-repeated-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    /// <summary>The fan-out scenario's real sequence: two stream re-points, three arranged events.</summary>
    private static readonly (string StepId, string Kind, string Text)[] Sequence =
    [
        ("GivenNoEventsFor", "Given", "no events for Appointment \"2542…\""),
        ("GivenEventOccurred", "Given", "HomeCheckAppointmentProposed occurred"),
        ("GivenEventOccurred", "Given", "AppointmentConfirmed occurred"),
        ("GivenNoEventsFor", "Given", "no events for Appointment \"6221…\""),
        ("GivenEventOccurred", "Given", "SurrenderIntakeAppointmentProposed occurred"),
        ("ThenReadModelWithIdContains", "Then", "the AppointmentsQueue read model … contains"),
    ];

    private RunDetail detail()
    {
        var t0 = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        using var registry = new MonitorRunRegistry(_dataPath);

        var events = new List<MonitorEvent>
        {
            new RunStarted(run, "CritterCrush.Specs", "/repo", "main", "mtp-host", t0, 1),
            new ScenarioStarted(run, Uid, "AppointmentsQueue",
                "The queue counts appointments from every stream in the shelter", 1, t0, TotalSteps: 6),
        };

        for (var i = 0; i < Sequence.Length; i++)
        {
            var (stepId, kind, text) = Sequence[i];
            events.Add(new StepStarted(run, Uid, stepId, kind, text, StepNumber: i + 1, TotalSteps: 6));
            events.Add(new StepFinished(run, Uid, stepId, "success", (i + 1) * 10, null));
        }

        registry.Record(events.ToArray());
        return RunEndpoints.Find(run, registry).ShouldBeOfType<Ok<RunDetail>>().Value.ShouldNotBeNull();
    }

    [Fact]
    public void every_occurrence_of_a_repeated_step_is_recorded_once()
    {
        var steps = detail().Scenarios.Single().Steps;

        steps.Count().ShouldBe(6);
        steps.Select(x => x.Text).ShouldBe(Sequence.Select(x => x.Text).ToList());
    }

    [Fact]
    public void no_step_is_left_running_in_a_scenario_that_finished()
    {
        var steps = detail().Scenarios.Single().Steps;

        // The headline of #322 — and the reason it went unnoticed: the scenario's own outcome is
        // computed elsewhere, so a green scenario with four steps mid-flight contradicted nothing
        // anybody was looking at.
        steps.Where(x => x.Status == "running").ShouldBeEmpty();
        steps.Select(x => x.DurationMs).ShouldBe([10, 20, 30, 40, 50, 60]);
    }

    [Fact]
    public void each_finish_pairs_with_the_occurrence_that_was_still_running()
    {
        var repoints = detail().Scenarios.Single().Steps
            .Where(x => x.StepId == "GivenNoEventsFor")
            .ToList();

        // Not a first-wins or last-wins collapse: the second re-point carries ITS duration (40),
        // and its own text. The console used to render one row with the last text and the first
        // duration, welded together.
        repoints.Select(x => x.DurationMs).ShouldBe([10, 40]);
        repoints.Select(x => x.Text).ShouldBe([Sequence[0].Text, Sequence[3].Text]);
    }
}
