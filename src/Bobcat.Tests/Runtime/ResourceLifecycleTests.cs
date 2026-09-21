using Bobcat.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// <see cref="ITestResource"/> is an <see cref="IHostedService"/>, so teardown is
/// <c>StopAsync</c> and the interface's default <c>DisposeAsync</c> delegates to it. These pin
/// that delegation: without it a resource that writes only the two hosted-service verbs would be
/// started and never stopped, and nothing else in the suite would notice.
/// </summary>
public class ResourceLifecycleTests
{
    /// <summary>The minimal resource under the new contract — no DisposeAsync at all.</summary>
    private sealed class StopOnlyResource(string name, List<string> log) : ITestResource
    {
        public string Name { get; } = name;
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            log.Add($"{Name}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            log.Add($"{Name}:stop");
            return Task.CompletedTask;
        }

        public Task ResetBetweenScenarios() => Task.CompletedTask;
    }

    [Fact]
    public void a_test_resource_is_a_hosted_service()
        => typeof(IHostedService).IsAssignableFrom(typeof(ITestResource)).ShouldBeTrue();

    [Fact]
    public async Task disposing_the_suite_stops_a_resource_that_never_wrote_a_disposer()
    {
        var log = new List<string>();
        var suite = new TestSuite();
        suite.AddResource(new StopOnlyResource("database", log));

        await suite.StartAll();
        await suite.DisposeAsync();

        log.ShouldBe(["database:start", "database:stop"]);
    }

    [Fact]
    public async Task await_using_over_such_a_resource_stops_it_too()
    {
        var log = new List<string>();

        await using (var resource = new StopOnlyResource("broker", log))
        {
            await resource.StartAsync(CancellationToken.None);
        }

        log.ShouldBe(["broker:start", "broker:stop"]);
    }

    [Fact]
    public async Task suite_teardown_stops_in_reverse_registration_order()
    {
        var log = new List<string>();
        var suite = new TestSuite();
        suite.AddResource(new StopOnlyResource("first", log));
        suite.AddResource(new StopOnlyResource("second", log));

        await suite.StartAll();
        await suite.DisposeAsync();

        log.ShouldBe(["first:start", "second:start", "second:stop", "first:stop"]);
    }

    // ── HostResource ────────────────────────────────────────────────────────

    private sealed class RecordingService(List<string> log) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            log.Add("host:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            log.Add("host:stop");
            return Task.CompletedTask;
        }
    }

    private static HostResource hostResource(List<string> log) => new(() =>
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IHostedService>(new RecordingService(log));
        return builder.Build();
    });

    [Fact]
    public async Task host_resource_stops_the_host_it_started()
    {
        var log = new List<string>();
        var resource = hostResource(log);

        await resource.StartAsync(CancellationToken.None);
        log.ShouldBe(["host:start"]);

        await resource.StopAsync(CancellationToken.None);
        log.ShouldBe(["host:start", "host:stop"]);
    }

    [Fact]
    public async Task stopping_a_host_resource_twice_is_a_no_op_the_second_time()
    {
        var log = new List<string>();
        var resource = hostResource(log);

        await resource.StartAsync(CancellationToken.None);
        await resource.StopAsync(CancellationToken.None);

        // The disposed host must not be stopped again — TestSuite disposes, and DisposeAsync
        // routes back here.
        await Should.NotThrowAsync(() => resource.StopAsync(CancellationToken.None));
        await Should.NotThrowAsync(async () => await resource.DisposeAsync());

        log.ShouldBe(["host:start", "host:stop"]);
    }

    [Fact]
    public async Task disposing_a_host_resource_stops_the_host()
    {
        var log = new List<string>();
        var resource = hostResource(log);

        await resource.StartAsync(CancellationToken.None);
        await resource.DisposeAsync();

        log.ShouldBe(["host:start", "host:stop"]);
    }
}
