using Bobcat.Console.EventModel;
using Bobcat.Console.Hosting;
using Bobcat.Monitoring;
using Bobcat.Supervisor.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// Issue #200: a <c>bobcat run</c> outlived its session by 20h52m, wedged on a port, and failed
/// the next repository's gate with <c>AddressInUseException</c>. Nothing in the process could
/// have ended it — JasperFx's <c>run</c> blocks on an untimed wait released only by a Ctrl-C a
/// detached process never receives — so the console has to decide for itself when it is no longer
/// doing anything for anybody.
/// </summary>
public class IdleShutdownTests
{
    private sealed class StubLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public bool Stopped { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped = true;
    }

    private static IConfiguration configuration(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)))
            .Build();

    [Fact]
    public void a_console_nobody_has_asked_anything_of_stops_itself()
    {
        var time = new FakeTimeProvider();
        var activity = new ConsoleActivity(time);
        var lifetime = new StubLifetime();
        var service = new IdleShutdownService(activity, lifetime, NullLogger<IdleShutdownService>.Instance,
            TimeSpan.FromHours(2));

        time.Advance(TimeSpan.FromMinutes(119));
        service.StopIfIdle().ShouldBeFalse();
        lifetime.Stopped.ShouldBeFalse();

        time.Advance(TimeSpan.FromMinutes(2));
        service.StopIfIdle().ShouldBeTrue();
        lifetime.Stopped.ShouldBeTrue();
    }

    [Fact]
    public void any_request_at_all_resets_the_window()
    {
        var time = new FakeTimeProvider();
        var activity = new ConsoleActivity(time);
        var lifetime = new StubLifetime();
        var service = new IdleShutdownService(activity, lifetime, NullLogger<IdleShutdownService>.Instance,
            TimeSpan.FromHours(2));

        time.Advance(TimeSpan.FromHours(3));

        // One publisher POSTing to /api/ingest, one SPA asset, one MCP call — the middleware does
        // not care which, and neither does this.
        activity.Began();
        activity.Ended();

        service.StopIfIdle().ShouldBeFalse();
        time.Advance(TimeSpan.FromMinutes(119));
        service.StopIfIdle().ShouldBeFalse();
    }

    [Fact]
    public void an_open_dashboard_never_starts_the_window_at_all()
    {
        // THE case an idle ceiling must not break, and the reason it is safe on by default: a
        // browser with the console open holds a SignalR connection, which is one request that
        // never completes. A developer who leaves the console running all day between test runs
        // never meets this timeout.
        var time = new FakeTimeProvider();
        var activity = new ConsoleActivity(time);
        var lifetime = new StubLifetime();
        var service = new IdleShutdownService(activity, lifetime, NullLogger<IdleShutdownService>.Instance,
            TimeSpan.FromHours(2));

        activity.Began();
        time.Advance(TimeSpan.FromDays(1));

        activity.IdleFor.ShouldBe(TimeSpan.Zero);
        service.StopIfIdle().ShouldBeFalse();
        lifetime.Stopped.ShouldBeFalse();

        // ...and the clock starts from the moment the last one drops, not from before it.
        activity.Ended();
        time.Advance(TimeSpan.FromMinutes(119));
        service.StopIfIdle().ShouldBeFalse();
    }

    [Fact]
    public void zero_runs_until_stopped()
    {
        var time = new FakeTimeProvider();
        var service = new IdleShutdownService(new ConsoleActivity(time), new StubLifetime(),
            NullLogger<IdleShutdownService>.Instance, TimeSpan.Zero);

        time.Advance(TimeSpan.FromDays(30));
        service.StopIfIdle().ShouldBeFalse();
    }

    [Fact]
    public void the_window_is_configuration_then_environment_then_two_hours()
    {
        IdleShutdownService.IdleTimeoutFrom(configuration()).ShouldBe(IdleShutdownService.DefaultIdleTimeout);
        IdleShutdownService.IdleTimeoutFrom(configuration(("Monitor:IdleMinutes", "45")))
            .ShouldBe(TimeSpan.FromMinutes(45));

        try
        {
            Environment.SetEnvironmentVariable(IdleShutdownService.IdleVariable, "5");
            IdleShutdownService.IdleTimeoutFrom(configuration()).ShouldBe(TimeSpan.FromMinutes(5));

            // Configuration wins over the environment, same order as the retention knobs.
            IdleShutdownService.IdleTimeoutFrom(configuration(("Monitor:IdleMinutes", "45")))
                .ShouldBe(TimeSpan.FromMinutes(45));
        }
        finally
        {
            Environment.SetEnvironmentVariable(IdleShutdownService.IdleVariable, null);
        }
    }
}

/// <summary>
/// The console binds the address every publisher probes. Two constants say it — one on each side
/// of the layering rule, since no Bobcat.* library may reference the console and the console does
/// not reference Bobcat — and a publisher probing an address the server does not bind is a
/// console that silently sees nothing, which is exactly how issue #200's orphan went unnoticed
/// on :5000 for a day.
/// </summary>
public class ConsoleUrlAgreementTests
{
    [Fact]
    public void the_server_binds_what_the_publisher_probes()
    {
        EventModelWatchPlan.DefaultConsoleUrl.ShouldBe(MonitorPublisher.DefaultUrl);
    }
}
