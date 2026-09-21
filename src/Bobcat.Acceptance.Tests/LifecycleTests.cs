using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class LifecycleTests
{
    private static BobcatRunner buildRunner(params IHostedService[] globals)
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(Lifecycle_Feature.Define());
        runner.Resources.Add(new HostResource(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddScoped<ISessionMarker, SessionMarker>();
            builder.Services.AddSingleton<IAppMarker, AppMarker>();
            return builder.Build();
        }));

        foreach (var global in globals) runner.Resources.Add(global);

        return runner;
    }

    private static void shouldHaveNoFailures(SuiteResults results)
    {
        var failed = results.Features
            .SelectMany(f => f.Scenarios)
            .SelectMany(s => s.Results.Steps.Select(step => (s.Title, step)))
            .Where(x => x.step.StepStatus is ResultStatus.failed or ResultStatus.error)
            .Select(x => $"{x.Title} / {x.step.StepText}")
            .ToList();

        failed.ShouldBeEmpty();
    }

    [Fact]
    public async Task discovered_hooks_run_at_the_right_level_and_in_the_right_scope()
    {
        LifecycleFixture.Reset();

        var results = await buildRunner().RunAll();

        shouldHaveNoFailures(results);

        LifecycleFixture.BeforeAllCount.ShouldBe(1);
        LifecycleFixture.AfterAllCount.ShouldBe(1);
        LifecycleFixture.BeforeEachCount.ShouldBe(2);
        LifecycleFixture.AfterEachCount.ShouldBe(2);
    }

    [Fact]
    public async Task a_registered_hosted_service_runs_once_around_the_whole_run()
    {
        LifecycleFixture.Reset();

        var action = new RecordingService();
        var results = await buildRunner(action).RunAll();

        shouldHaveNoFailures(results);

        action.StartCount.ShouldBe(1);
        action.StopCount.ShouldBe(1);

        // Start lands before the first feature hook, Stop after the last one.
        action.BeforeAllCountAtStart.ShouldBe(0);
        action.AfterAllCountAtStop.ShouldBe(1);
    }

    /// <summary>
    /// The ordering change that came with <c>IGlobalAction</c>'s removal: there is now ONE list,
    /// so a hosted service and a resource order against each other by registration rather than
    /// every resource starting before every global action.
    /// </summary>
    [Fact]
    public async Task hosted_services_start_in_order_and_stop_in_reverse()
    {
        var log = new List<string>();
        var resources = new TestResources();
        resources.Add(new OrderedService("first", log));
        resources.Add(new OrderedService("second", log));

        await resources.StartAll();
        await resources.DisposeAsync();

        log.ShouldBe(["first:start", "second:start", "second:stop", "first:stop"]);
    }

    [Fact]
    public async Task a_hosted_service_that_will_not_start_is_catastrophic()
    {
        var resources = new TestResources();
        resources.Add(new ThrowingService());

        var ex = await Should.ThrowAsync<SpecCatastrophicException>(resources.StartAll());
        ex.Message.ShouldContain("ThrowingService");
    }

    /// <summary>
    /// A resource and a plain hosted service share one registration order, which is the whole
    /// point of folding IGlobalAction into IHostedService. Registering the service FIRST means it
    /// starts first — something IGlobalAction could not express.
    /// </summary>
    [Fact]
    public async Task resources_and_plain_services_share_one_ordering()
    {
        var log = new List<string>();
        var resources = new TestResources();
        resources.Add(new OrderedService("seed", log));
        resources.Add(new OrderedResource("database", log));

        await resources.StartAll();
        await resources.DisposeAsync();

        log.ShouldBe(["seed:start", "database:start", "database:stop", "seed:stop"]);
    }

    private class RecordingService : IHostedService
    {
        public int StartCount;
        public int StopCount;
        public int BeforeAllCountAtStart;
        public int AfterAllCountAtStop;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            BeforeAllCountAtStart = LifecycleFixture.BeforeAllCount;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            AfterAllCountAtStop = LifecycleFixture.AfterAllCount;
            return Task.CompletedTask;
        }
    }

    private class OrderedService(string name, List<string> log) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            log.Add($"{name}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            log.Add($"{name}:stop");
            return Task.CompletedTask;
        }
    }

    private class OrderedResource(string name, List<string> log) : ITestResource
    {
        public string Name => name;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            log.Add($"{name}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            log.Add($"{name}:stop");
            return Task.CompletedTask;
        }

        public Task ResetBetweenScenarios() => Task.CompletedTask;
    }

    private class ThrowingService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("seed data unavailable");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
