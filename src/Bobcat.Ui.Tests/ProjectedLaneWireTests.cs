using System.Text.Json;
using Bobcat.Ui.Contracts;
using Bobcat.Ui.Runs;
using Shouldly;

namespace Bobcat.Ui.Tests;

/// <summary>
/// What Bobcat 0.60.1's projected lane really puts on the wire, replayed from a run captured off a
/// live Stoat during the 2026-10-12 CritterCrush rehearsal: three passing specs of a supervised
/// run, verbatim (<c>Fixtures/crittercrush-supervised-bobcat-0.60.1.ndjson</c>). Two things that
/// looked fine against hand-written events were wrong against this one.
/// </summary>
/// <remarks>
/// The dashboard's fold is pinned to the same capture in <c>runs-store-projected-wire.test.ts</c>.
/// </remarks>
public class ProjectedLaneWireTests
{
    private static readonly JsonSerializerOptions wire = new(JsonSerializerDefaults.Web);

    private static IReadOnlyList<MonitorEvent> captured()
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "crittercrush-supervised-bobcat-0.60.1.ndjson"))
            .Where(line => line.Trim().Length > 0)
            .Select(line => JsonSerializer.Deserialize<MonitorEvent>(line, wire)!)
            .ToList();

    private static RunProjection fold(IEnumerable<MonitorEvent> events)
    {
        RunProjection? projection = null;
        foreach (var @event in events)
        {
            projection ??= new RunProjection(@event.RunId);
            projection.Apply(@event);
        }

        return projection!;
    }

    [Fact]
    public void the_capture_really_carries_both_streams_under_different_keys()
    {
        // The premise of the fix, checked against the wire rather than assumed: the supervisor
        // forwards every test under xUnit's hash of the method, never the spec identity.
        var events = captured();
        events.OfType<ScenarioFinished>().Select(e => e.Uid).ShouldAllBe(uid => uid.Contains('/'));
        events.OfType<TestFinished>().Count().ShouldBe(3);
        events.OfType<TestFinished>().Select(e => e.Uid).ShouldAllBe(uid => !uid.Contains('/'));
    }

    [Fact]
    public void a_supervised_projected_run_counts_each_test_once_not_twice()
    {
        // The rehearsal's run card: 19 tests read as 38 scenarios.
        var run = fold(captured());

        run.Scenarios.Count.ShouldBe(3);
        run.Scenarios.ShouldAllBe(s => s.Feature != "" && s.Uid.Contains('/'));
        run.Scenarios.Count(s => s.Outcome != null).ShouldBe(3);
    }

    [Fact]
    public void the_count_is_the_same_whichever_stream_arrives_first()
    {
        var events = captured();
        var forwardedFirst = events.OfType<TestStarted>().Cast<MonitorEvent>()
            .Concat(events.OfType<TestFinished>())
            .Concat(events.Where(e => e is not (TestStarted or TestFinished)));

        fold(forwardedFirst).Scenarios.Count.ShouldBe(3);
    }

    [Fact]
    public void a_forwarded_test_no_worker_scenario_mirrors_stays_on_the_board()
    {
        // A plain [Fact] beside the specs in a spec assembly: only the supervisor saw it, and a
        // failing one must not be hidden by the de-duplication.
        var events = captured().ToList();
        var runId = events[0].RunId;
        events.Add(new TestFinished(runId, "0f0f0f", "CritterCrush.Specs.Plumbing.the_host_boots", "Failed", 12, 0,
            DateTimeOffset.Parse("2026-10-12T19:22:36Z")));

        var run = fold(events);

        run.Scenarios.Count.ShouldBe(4);
        run.Scenarios.Single(s => s.Uid == "0f0f0f").Outcome.ShouldBe("Failed");
    }

    [Fact]
    public void two_scenarios_with_one_title_are_told_apart_by_the_class_and_never_guessed()
    {
        var runId = Guid.NewGuid();
        var at = DateTimeOffset.Parse("2026-10-12T19:00:00Z");
        var run = fold([
            new ScenarioFinished(runId, "Orders/happy path", "CleanPass", 1, 5, null),
            new ScenarioFinished(runId, "Refunds/happy path", "CleanPass", 1, 5, null),
            // Matches Orders by class.
            new TestFinished(runId, "a1", "Specs.Orders.happy_path", "Passed", 5, 0, at),
            // Same title, a class that is neither feature: two equally good candidates, so kept.
            new TestFinished(runId, "b2", "Specs.Misc.happy_path", "Passed", 5, 0, at)
        ]);

        run.Scenarios.Select(s => s.Uid).OrderBy(u => u, StringComparer.Ordinal)
            .ShouldBe(["Orders/happy path", "Refunds/happy path", "b2"]);
    }

    [Theory]
    [InlineData("Specs.Calc.adds_two(a: 1.5, b: 2)", "Calc", "adds_two")]
    [InlineData("Specs.Calc.adds_two", "Calc", "adds_two")]
    [InlineData("adds_two", "", "adds_two")]
    public void a_display_name_splits_into_class_and_method(string displayName, string className, string method)
        => ForwardedTestMirror.Split(displayName).ShouldBe((className, method));

    [Fact]
    public void every_step_of_a_passing_projected_spec_reads_as_passed()
    {
        // The projected lane says "Passed", where the Gherkin lane said "success": a reader that
        // knew only the second drew a red ✗ on every step of a green run.
        var run = fold(captured());

        var steps = run.Scenarios.SelectMany(s => s.Steps).ToList();
        steps.Count.ShouldBe(11);
        steps.ShouldAllBe(s => s.Status == "Passed");
        steps.ShouldAllBe(s => StepVerdict.IsPassed(s.Status));
        steps.ShouldAllBe(s => !StepVerdict.IsFailed(s.Status));
    }

    [Fact]
    public void the_ctrf_export_of_a_passing_projected_run_has_no_failed_step()
    {
        using var doc = JsonDocument.Parse(CtrfExport.Render(fold(captured())));
        var tests = doc.RootElement.GetProperty("results").GetProperty("tests").EnumerateArray().ToArray();

        tests.Length.ShouldBe(3);
        tests.SelectMany(t => t.GetProperty("steps").EnumerateArray())
            .Select(s => s.GetProperty("status").GetString())
            .ShouldAllBe(status => status == "passed");
    }

    [Theory]
    [InlineData("Passed", true)]
    [InlineData("passed", true)]
    [InlineData("success", true)]
    [InlineData("ok", true)]
    [InlineData("Failed", false)]
    [InlineData("failed", false)]
    [InlineData("error", false)]
    [InlineData("missing", false)]
    public void both_lanes_step_vocabularies_are_read(string status, bool passed)
    {
        StepVerdict.IsPassed(status).ShouldBe(passed);
        StepVerdict.IsFailed(status).ShouldBe(!passed);
    }

    [Fact]
    public void a_running_step_is_neither_passed_nor_failed()
    {
        StepVerdict.IsPassed(StepProjection.Running).ShouldBeFalse();
        StepVerdict.IsFailed(StepProjection.Running).ShouldBeFalse();
    }
}
