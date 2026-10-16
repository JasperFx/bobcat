using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Tests.Monitoring;

/// <summary>
/// Every test here owns the <c>BOBCAT_MONITOR</c> kill switch for its duration: the value the
/// process started with is saved, cleared so the publisher is enabled, and restored afterwards.
/// Without that these tests answer to whatever the ambient environment says — CI sets
/// <c>BOBCAT_MONITOR=0</c> so the spec hosts it collects never publish, and two of these tests
/// quietly asserted a publisher that the switch had already refused to build.
/// </summary>
[Collection("monitor-kill-switch")]
public class MonitorPublisherTests : IDisposable
{
    private readonly string? _previousKillSwitch;

    public MonitorPublisherTests()
    {
        _previousKillSwitch = Environment.GetEnvironmentVariable(MonitorPublisher.KillSwitchVariable);
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, null);
    }

    public void Dispose()
        => Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, _previousKillSwitch);

    [Fact]
    public async Task try_connect_returns_null_quickly_when_nothing_is_listening()
    {
        // A port nothing listens on — connection refused, not a hang.
        var started = DateTime.UtcNow;
        var publisher = await MonitorPublisher.TryConnect("http://127.0.0.1:1");

        publisher.ShouldBeNull();
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task the_kill_switch_suppresses_even_the_probe()
    {
        using var host = new FakeMonitorHost();
        // Restored to the pre-test value by Dispose, never blindly to null.
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, "0");

        (await MonitorPublisher.TryConnect(host.Url)).ShouldBeNull();
    }

    [Fact]
    public async Task a_console_slower_than_the_default_probe_is_given_up_on_unless_one_is_expected()
    {
        // bobcat#589: the console saw the ping, the child's 250ms probe had already given up, and a
        // resident runner's commanded run published nothing at all.
        using var host = new FakeMonitorHost { PingDelay = TimeSpan.FromSeconds(1) };

        (await MonitorPublisher.TryConnect(host.Url)).ShouldBeNull();
        host.Pings.ShouldBe(1);

        // What a resident runner sets on every child it launches.
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, "1");
        await using var publisher = await MonitorPublisher.TryConnect(host.Url);
        publisher.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("TRUE", true)]
    public void only_an_explicit_opt_in_waits_longer_for_the_console(string? value, bool expected)
        => MonitorPublisher.ProbeTimeoutFor(value).ShouldBe(
            expected ? MonitorPublisher.ExpectedProbeTimeout : MonitorPublisher.DefaultProbeTimeout);

    [Fact]
    public async Task posted_events_arrive_in_batch_envelopes_with_snake_case_discriminators_and_camel_case_fields()
    {
        using var host = new FakeMonitorHost();
        var publisher = await MonitorPublisher.TryConnect(host.Url);
        publisher.ShouldNotBeNull();

        var runId = Guid.NewGuid();
        publisher.Post(new RunStarted(runId, "Suite", "/repo", "main", "in-process",
            DateTimeOffset.UtcNow, 3));
        publisher.Post(new StepStarted(runId, "F/s", "step-1", "Given", "a thing"));

        // DisposeAsync flushes what's queued before returning.
        await publisher.DisposeAsync();

        // Batching is opportunistic: the pump sends whatever is queued each time it wakes, so two
        // back-to-back posts travel as one batch on a warm process and as two on a cold one. The
        // contract is that every event arrives inside an "events" envelope — not how many
        // envelopes it takes — so that is what is asserted here.
        var batches = host.Batches;
        batches.ShouldNotBeEmpty();
        batches.ShouldAllBe(b => b.Contains("\"events\""));

        var everything = string.Join("\n", batches);
        everything.ShouldContain("\"type\":\"run_started\"");
        everything.ShouldContain("\"type\":\"step_started\"");
        everything.ShouldContain($"\"runId\":\"{runId}\"");
        everything.ShouldContain("\"totalScenarios\":3");
    }

    [Fact]
    public async Task disposing_with_a_dead_monitor_does_not_hang()
    {
        using var host = new FakeMonitorHost();
        var publisher = await MonitorPublisher.TryConnect(host.Url);
        publisher.ShouldNotBeNull();

        // The monitor dies mid-run; the publisher must close out promptly regardless.
        host.Dispose();
        publisher.Post(new RunHeartbeat(Guid.NewGuid(), DateTimeOffset.UtcNow));

        var closing = publisher.DisposeAsync().AsTask();
        var finished = await Task.WhenAny(closing, Task.Delay(TimeSpan.FromSeconds(10)));
        finished.ShouldBe(closing);
    }
}
