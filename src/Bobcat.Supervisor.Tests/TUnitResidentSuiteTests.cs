using System.Text.Json;
using Bobcat.Residency;
using Bobcat.Runtime;
using Bobcat.Tests.Monitoring;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#470: a TUnit suite served by the resident runner, cold and warm, against the real
/// <c>Bobcat.TUnit.Tests</c> host (its <c>Specs/</c> namespace is a projected Bobcat suite on
/// TUnit). Cold runs by tree-node filter, which <see cref="SpecFilterArguments"/> builds only where
/// one path names the selection exactly; warm runs by platform uid, joined on the reported location.
/// </summary>
public class TUnitResidentSuiteTests : IDisposable
{
    private static readonly string host = WorkerHosts.Locate("Bobcat.TUnit.Tests");

    private const string ledgerOpening = "Ledger/opening a ledger";
    private const string ledgerClosing = "Ledger/closing a ledger";
    private const string auditOpening = "Audit/opening a ledger";
    private const string auditClosing = "Audit/closing a ledger";
    private const string auditTrail = "Audit trail/is kept";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "bobcat-tunit-resident", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* a temp directory is not worth failing a test over */ }
    }

    private Task<OutOfProcessResidentSuite> coldSuiteFor(FakeMonitorHost monitor)
        => OutOfProcessResidentSuite.For(host, monitorUrl: monitor.Url, listingDirectory: _directory);

    private Task<WarmProjectedResidentSuite> warmSuiteFor(FakeMonitorHost monitor)
        => WarmProjectedResidentSuite.For(host, monitorUrl: monitor.Url, listingDirectory: _directory);

    [Fact]
    public async Task a_tunit_suite_lists_its_identities_and_is_no_longer_refused_at_launch()
    {
        using var monitor = new FakeMonitorHost();
        var suite = await coldSuiteFor(monitor);

        suite.Suite.ShouldBe("Bobcat.TUnit.Tests");
        suite.Manifest.Framework.ShouldBe(SpecManifest.TUnitFramework);
        suite.SpecIdentities.ShouldBe(
            [auditTrail, auditClosing, auditOpening, ledgerClosing, ledgerOpening], ignoreOrder: true);
        suite.Modes.ShouldBe([RunnerWire.ColdMode]);

        await using var warm = await warmSuiteFor(monitor);
        warm.Modes.ShouldBe([RunnerWire.ColdMode, RunnerWire.WarmMode]);
    }

    [Theory]
    [InlineData(ledgerOpening)]
    // Two methods in one class: alternatives in the method segment.
    [InlineData(ledgerOpening, ledgerClosing)]
    // The same method name on two classes: alternatives in the class segment, and the classes'
    // other method (closing_a_ledger) must not run.
    [InlineData(ledgerOpening, auditOpening)]
    // A nested class, Outer+Inner.
    [InlineData(auditTrail)]
    public async Task a_cold_command_runs_exactly_the_tunit_specifications_it_names(params string[] identities)
    {
        using var monitor = new FakeMonitorHost();
        var suite = await coldSuiteFor(monitor);

        await suite.Run("c-tunit", SpecSelection.Of(identities), RunnerWire.ColdMode, default);

        await eventually(() => eventsNamed(monitor, "run_finished").Count >= 1, "the child never closed its run");

        var started = eventsNamed(monitor, "run_started");
        started.Count.ShouldBe(1);
        started[0].GetProperty("command").GetString().ShouldBe("c-tunit");

        scenariosOf(monitor).ShouldBe(identities, ignoreOrder: true);
    }

    [Fact]
    public async Task a_cold_selection_no_single_tree_node_path_can_name_is_refused_before_anything_runs()
    {
        // LedgerSpecs.opening_a_ledger + AuditSpecs.closing_a_ledger: per-segment alternatives would
        // select all four tests, so there is no exact filter and nothing is launched.
        using var monitor = new FakeMonitorHost();
        var suite = await coldSuiteFor(monitor);

        var refusal = await Should.ThrowAsync<NotSupportedException>(
            () => suite.Run("c-mixed", SpecSelection.Of(ledgerOpening, auditClosing), RunnerWire.ColdMode, default));

        refusal.Message.ShouldContain("warm command");
        eventsNamed(monitor, "run_started").ShouldBeEmpty();
    }

    [Fact]
    public async Task two_warm_tunit_commands_are_two_runs_in_one_live_host()
    {
        // The warm path runs by uid, so the selection the cold path refuses is fine here; and
        // Bobcat.TUnit's last-test-in-session receiver closes the run per request, so each command
        // is its own run with its own counts.
        using var monitor = new FakeMonitorHost();
        await using var suite = await warmSuiteFor(monitor);

        await suite.Run("c-warm-1", SpecSelection.Of(ledgerOpening, auditClosing), RunnerWire.WarmMode, default);
        await eventually(() => eventsNamed(monitor, "run_finished").Count >= 1, "the first warm command never closed its run");

        await suite.Run("c-warm-2", SpecSelection.Of(auditTrail), RunnerWire.WarmMode, default);
        await eventually(() => eventsNamed(monitor, "run_finished").Count >= 2, "the second warm command never closed its run");

        suite.WarmUnavailable.ShouldBeNull();

        var started = eventsNamed(monitor, "run_started");
        started.Select(e => e.GetProperty("command").GetString()).ShouldBe(["c-warm-1", "c-warm-2"]);
        started.Select(e => e.GetProperty("runId").GetString()).Distinct().Count().ShouldBe(2);

        // Each run counted its own scenarios, which is only true if the bracket closed AFTER the
        // session's last scenario ended and not before.
        eventsNamed(monitor, "run_finished").Select(e => e.GetProperty("passed").GetInt32()).ShouldBe([2, 1]);

        scenariosOf(monitor).ShouldBe([ledgerOpening, auditClosing, auditTrail], ignoreOrder: true);
    }

    [Fact]
    public async Task a_warm_tunit_command_through_the_runner_is_accepted_and_published_as_the_command()
    {
        using var monitor = new FakeMonitorHost();
        await using var suite = await warmSuiteFor(monitor);

        await using var runner = new ResidentRunner(suite, new ResidentRunnerOptions
        {
            Url = monitor.Url,
            RunnerId = "warm-tunit-runner"
        });

        (await runner.Register()).ShouldBeTrue();

        await runner.Handle(CloudEvent.From(
            "stoat", RunnerWire.RunCommandType, new RunCommand("c-runner", [auditTrail], RunnerWire.WarmMode)));

        await eventually(() => eventsNamed(monitor, "run_finished").Count >= 1, "the warm command never closed its run");

        eventsNamed(monitor, "run_started").Single().GetProperty("command").GetString().ShouldBe("c-runner");
        scenariosOf(monitor).ShouldBe([auditTrail]);
    }

    private static IReadOnlyList<string?> scenariosOf(FakeMonitorHost monitor)
        => eventsNamed(monitor, "scenario_finished").Select(e => e.GetProperty("uid").GetString()).ToList();

    private static async Task eventually(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException(because);
    }

    private static IReadOnlyList<JsonElement> eventsNamed(FakeMonitorHost monitor, string name)
    {
        var found = new List<JsonElement>();

        foreach (var batch in monitor.Batches)
        {
            using var document = JsonDocument.Parse(batch);
            var root = document.RootElement;

            var events = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : root.TryGetProperty("events", out var nested) && nested.ValueKind == JsonValueKind.Array
                    ? nested.EnumerateArray()
                    : default;

            foreach (var @event in events)
            {
                if (@event.TryGetProperty("type", out var type) && type.GetString() == name) found.Add(@event.Clone());
            }
        }

        return found;
    }
}
