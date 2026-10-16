using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Tests.Monitoring;

/// <summary>
/// Bobcat#530 — <c>BOBCAT_MONITOR_URL</c> is a list, and a run publishes to every target that
/// answered. Driven over real HTTP against <see cref="FakeMonitorHost"/>, because the claim under
/// test is about what one target's absence costs the others and a seam in front of the transport
/// would test the seam instead.
/// </summary>
/// <remarks>
/// In the <c>monitor-kill-switch</c> collection with the other two publisher classes: these own
/// both <c>BOBCAT_MONITOR</c> and <c>BOBCAT_MONITOR_URL</c> for their duration, and both are one
/// value for the whole test process.
/// </remarks>
[Collection("monitor-kill-switch")]
public class MonitorTargetsTests : IDisposable
{
    /// <summary>A port nothing listens on: a refused connection, which is not a hang.</summary>
    private const string dead = "http://127.0.0.1:1";

    private readonly string? _previousKillSwitch;
    private readonly string? _previousUrl;

    public MonitorTargetsTests()
    {
        _previousKillSwitch = Environment.GetEnvironmentVariable(MonitorPublisher.KillSwitchVariable);
        _previousUrl = Environment.GetEnvironmentVariable(MonitorPublisher.UrlVariable);
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, _previousKillSwitch);
        Environment.SetEnvironmentVariable(MonitorPublisher.UrlVariable, _previousUrl);
    }

    [Theory]
    [InlineData(null, "http://localhost:5525")]
    [InlineData("", "http://localhost:5525")]
    [InlineData("http://localhost:5525", "http://localhost:5525")]
    [InlineData("http://localhost:5525,http://localhost:6000", "http://localhost:5525|http://localhost:6000")]
    [InlineData("http://localhost:5525;http://localhost:6000", "http://localhost:5525|http://localhost:6000")]
    [InlineData(" http://localhost:5525 , http://localhost:6000 ", "http://localhost:5525|http://localhost:6000")]
    [InlineData(",,http://localhost:5525,,", "http://localhost:5525")]
    // A doubled target would double every event on the console reading it.
    [InlineData("http://localhost:5525,http://localhost:5525", "http://localhost:5525")]
    // Compared on the Uri each entry resolves to, so a trailing slash is not a second target.
    [InlineData("http://localhost:5525,http://localhost:5525/", "http://localhost:5525")]
    [InlineData("  ,  ", "http://localhost:5525")]
    public void the_url_variable_is_read_as_a_list(string? value, string expected)
        => MonitorPublisher.ParseUrls(value).ShouldBe(expected.Split('|'));

    /// <summary>
    /// An entry that is not a URL is kept rather than refusing the whole list: it fails at the
    /// probe, the way an unreachable console does, and refusing the list over it would cost the
    /// good entries their events.
    /// </summary>
    [Fact]
    public void an_entry_that_is_not_a_url_is_left_to_fail_at_the_probe()
        => MonitorPublisher.ParseUrls("not a url,http://localhost:6000")
            .ShouldBe(["not a url", "http://localhost:6000"]);

    /// <summary>
    /// The one caller that needs a single origin is the resident runner's command stream, and the
    /// first entry is it.
    /// </summary>
    [Fact]
    public void the_singular_url_is_the_first_target()
    {
        Environment.SetEnvironmentVariable(
            MonitorPublisher.UrlVariable, "http://localhost:7001,http://localhost:7002");

        MonitorPublisher.ResolveUrls().ShouldBe(["http://localhost:7001", "http://localhost:7002"]);
        MonitorPublisher.ResolveUrl().ShouldBe("http://localhost:7001");
    }

    [Fact]
    public async Task every_target_that_answered_receives_every_event()
    {
        using var first = new FakeMonitorHost();
        using var second = new FakeMonitorHost();

        var targets = await MonitorTargets.TryConnect([first.Url, second.Url]);
        targets.ShouldNotBeNull();
        targets.Urls.ShouldBe([first.Url, second.Url]);

        var runId = Guid.NewGuid();
        targets.Post(new RunStarted(runId, "Suite", "/repo", "main", "in-process", DateTimeOffset.UtcNow, 1));
        await targets.DisposeAsync();

        foreach (var host in new[] { first, second })
        {
            var everything = string.Join("\n", host.Batches);
            everything.ShouldContain("\"type\":\"run_started\"");
            everything.ShouldContain($"\"runId\":\"{runId}\"");
        }
    }

    /// <summary>
    /// The invariant, now per target: an unreachable Stoat must not cost <c>bobcat ui</c> one
    /// event, and the probes run concurrently so a dead target does not even cost its timeout
    /// twice over.
    /// </summary>
    [Fact]
    public async Task a_target_nothing_is_listening_on_costs_the_live_ones_nothing()
    {
        using var host = new FakeMonitorHost();

        var started = DateTime.UtcNow;
        var targets = await MonitorTargets.TryConnect([dead, host.Url, dead]);
        targets.ShouldNotBeNull();

        targets.Urls.ShouldBe([host.Url]);
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(3));

        targets.Post(new RunHeartbeat(Guid.NewGuid(), DateTimeOffset.UtcNow));
        await targets.DisposeAsync();

        host.Batches.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task nothing_answering_at_all_is_no_publisher_exactly_as_before()
        => (await MonitorTargets.TryConnect([dead, "http://127.0.0.1:2"])).ShouldBeNull();

    [Fact]
    public async Task the_kill_switch_suppresses_even_the_probes()
    {
        using var host = new FakeMonitorHost();
        // Restored to the pre-test value by Dispose, never blindly to null.
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, "0");

        (await MonitorTargets.TryConnect([host.Url])).ShouldBeNull();
    }

    /// <summary>
    /// A target that dies mid-run gives up on its own counter and its own channel, so the other
    /// target's stream is untouched — and closing the run does not wait on the dead one twice.
    /// </summary>
    [Fact]
    public async Task a_target_that_dies_mid_run_neither_stops_the_others_nor_hangs_the_close()
    {
        var dying = new FakeMonitorHost();
        using var survivor = new FakeMonitorHost();

        var targets = await MonitorTargets.TryConnect([dying.Url, survivor.Url]);
        targets.ShouldNotBeNull();

        dying.Dispose();

        var runId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            targets.Post(new RunHeartbeat(runId, DateTimeOffset.UtcNow));
        }

        var closing = targets.DisposeAsync().AsTask();
        var finished = await Task.WhenAny(closing, Task.Delay(TimeSpan.FromSeconds(10)));
        finished.ShouldBe(closing);

        string.Join("\n", survivor.Batches).ShouldContain($"\"runId\":\"{runId}\"");
    }

    /// <summary>
    /// With no list given, the targets are the environment's — which is how every publisher in a
    /// run resolves them, the supervisor's workers included.
    /// </summary>
    [Fact]
    public async Task the_targets_default_to_the_environments_list()
    {
        using var first = new FakeMonitorHost();
        using var second = new FakeMonitorHost();
        Environment.SetEnvironmentVariable(
            MonitorPublisher.UrlVariable, $"{first.Url},{dead},{second.Url}");

        var targets = await MonitorTargets.TryConnect();
        targets.ShouldNotBeNull();
        await using (targets)
        {
            targets.Urls.ShouldBe([first.Url, second.Url]);
        }
    }
}
