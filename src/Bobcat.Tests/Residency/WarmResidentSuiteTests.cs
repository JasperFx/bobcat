using Bobcat.Engine;
using Bobcat.Residency;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Residency;

/// <summary>
/// Issue #393: a warm resident suite runs command after command inside one booted host — and a
/// second command still sees none of the first's state.
/// </summary>
public class WarmResidentSuiteTests
{
    public class WarmFixture : Fixture;

    private sealed class CountingResource : ITestResource
    {
        public int Started, Resets, Disposed;
        public bool FailOnStart;
        public bool FailOnReset;

        /// <summary>
        /// Persistent state the scenarios write into. It is what a leak would show up as: the reset
        /// clears it, so a scenario finding it dirty means the bracket did not run.
        /// </summary>
        public readonly List<string> Rows = new();

        public string Name => "counting";

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (FailOnStart) throw new SpecCatastrophicException("connection refused");
            Started++;
            return Task.CompletedTask;
        }

        public Task ResetBetweenScenarios()
        {
            if (FailOnReset) throw new InvalidOperationException("truncate deadlocked");

            Resets++;
            Rows.Clear();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

    private static readonly List<string> seen = new();

    public WarmResidentSuiteTests() => seen.Clear();

    /// <summary>
    /// Every scenario records what it found in the resource BEFORE writing to it, so a leak is
    /// visible as a non-empty reading rather than having to be inferred.
    /// </summary>
    private static FeatureDefinition feature(CountingResource resource, string title, params string[] scenarios)
        => new(title, typeof(WarmFixture),
            scenarios.Select(s => new ScenarioDefinition(s, [], (_, plan) =>
                plan.Add(new DelegateExecutionStep("step", StepKind.Then, s, (_, result, _) =>
                {
                    seen.Add($"{title}/{s} found [{string.Join(",", resource.Rows)}]");
                    resource.Rows.Add(s);
                    result.MarkSuccess();
                    return Task.CompletedTask;
                })))).ToArray());

    private static BobcatResidentSuite suiteOver(CountingResource resource, params FeatureDefinition[] features)
        => new(runner =>
        {
            foreach (var f in features) runner.AddFeature(f);
            runner.Resources.Add(resource);
        })
        { Quiet = true };

    [Fact]
    public async Task two_commands_in_a_row_boot_once_and_the_second_sees_none_of_the_first()
    {
        var resource = new CountingResource();
        await using var suite = suiteOver(
            resource, feature(resource, "Orders", "places an order", "cancels an order"));

        suite.Modes.ShouldBe(["cold", "warm"]);

        await suite.Run("c1", SpecSelection.Of("Orders/places an order"), RunnerWire.WarmMode, default);
        await suite.Run("c2", SpecSelection.Of("Orders/cancels an order"), RunnerWire.WarmMode, default);

        resource.Started.ShouldBe(1, "StartAll is paid once per warm session, not once per command");
        resource.Disposed.ShouldBe(0, "the session is still held between commands");

        // The assertion that fails if state leaks: the second command's scenario found the
        // resource empty, even though the first command's scenario had written to it.
        seen.ShouldBe([
            "Orders/places an order found []",
            "Orders/cancels an order found []"
        ]);

        resource.Resets.ShouldBe(2, "every scenario still gets the full per-scenario reset bracket");
    }

    [Fact]
    public async Task a_cold_command_is_still_cold_and_closes_the_warm_session()
    {
        // They cannot coexist: a booted host holds the port and the database a second one would
        // ask for, so "fresh everything" has to include tearing down what is up.
        var resource = new CountingResource();
        await using var suite = suiteOver(resource, feature(resource, "Orders", "places an order"));

        await suite.Run("c1", SpecSelection.Of("Orders/places an order"), RunnerWire.WarmMode, default);
        suite.IsWarm.ShouldBeTrue();

        await suite.Run("c2", SpecSelection.Of("Orders/places an order"), RunnerWire.ColdMode, default);

        suite.IsWarm.ShouldBeFalse();
        resource.Started.ShouldBe(2, "the cold command booted its own");
        resource.Disposed.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task a_failed_reset_withdraws_warm_and_says_why()
    {
        var resource = new CountingResource();
        await using var suite = suiteOver(
            resource, feature(resource, "Orders", "places an order", "cancels an order"));

        await suite.Run("c1", SpecSelection.Of("Orders/places an order"), RunnerWire.WarmMode, default);
        suite.WarmUnavailable.ShouldBeNull();

        resource.FailOnReset = true;
        await suite.Run("c2", SpecSelection.Of("Orders/cancels an order"), RunnerWire.WarmMode, default);

        suite.WarmUnavailable.ShouldNotBeNull().ShouldContain("truncate deadlocked");
        suite.Modes.ShouldBe(["cold"], "a monitor must stop being offered a mode it cannot have");

        // And the poisoned host is not left holding anything: nobody will run on it again.
        suite.IsWarm.ShouldBeFalse();
    }

    [Fact]
    public async Task warm_damage_does_not_stop_a_cold_command()
    {
        // Cold starts over from exactly the thing that poisoned the warm host, so it is the one
        // mode a reset failure says nothing about.
        var resource = new CountingResource();
        await using var suite = suiteOver(resource, feature(resource, "Orders", "places an order"));

        resource.FailOnReset = true;
        await suite.Run("c1", SpecSelection.Of("Orders/places an order"), RunnerWire.WarmMode, default);
        suite.WarmUnavailable.ShouldNotBeNull();

        resource.FailOnReset = false;
        seen.Clear();

        await suite.Run("c2", SpecSelection.Of("Orders/places an order"), RunnerWire.ColdMode, default);

        seen.ShouldHaveSingleItem().ShouldStartWith("Orders/places an order");
    }

    [Fact]
    public async Task a_warm_session_that_will_not_boot_withdraws_warm_and_reports_the_command_unrun()
    {
        // A suite whose resources will not come up will not come up on the next command either,
        // so the mode goes rather than being retried. The command itself is a failure the runner
        // reports — it genuinely did not run.
        var resource = new CountingResource { FailOnStart = true };
        await using var suite = suiteOver(resource, feature(resource, "Orders", "places an order"));

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => suite.Run("c1", SpecSelection.Of("Orders/places an order"), RunnerWire.WarmMode, default));

        failure.Message.ShouldContain("connection refused");
        suite.WarmUnavailable.ShouldNotBeNull().ShouldContain("connection refused");
        suite.Modes.ShouldBe(["cold"]);
    }

    [Fact]
    public async Task a_suite_that_does_not_offer_warm_runs_every_command_cold()
    {
        var resource = new CountingResource();
        var suite = new BobcatResidentSuite(runner =>
        {
            runner.AddFeature(feature(resource, "Orders", "places an order"));
            runner.Resources.Add(resource);
        })
        { Quiet = true, OffersWarm = false };

        await using var _ = suite;

        suite.Modes.ShouldBe(["cold"]);

        // Even asked for warm, nothing is held: the runner refuses an unoffered mode before this
        // is ever reached, and the suite does not quietly honour one either.
        await suite.Run("c1", SpecSelection.Of("Orders/places an order"), RunnerWire.WarmMode, default);

        suite.IsWarm.ShouldBeFalse();
        resource.Disposed.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task a_warm_suite_lists_its_identities_and_lane_like_any_other()
    {
        var resource = new CountingResource();
        await using var suite = suiteOver(
            resource,
            feature(resource, "Orders", "places an order"),
            feature(resource, "Stock", "counts"));

        suite.Lane.ShouldBe("gherkin");
        suite.SpecIdentities.ShouldBe(["Orders/places an order", "Stock/counts"]);
    }
}
