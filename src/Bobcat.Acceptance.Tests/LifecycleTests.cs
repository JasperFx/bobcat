using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class LifecycleTests
{
    private static BobcatRunner buildRunner(params IGlobalAction[] globals)
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(Lifecycle_Feature.Define());
        runner.Suite.AddResource(new HostResource(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddScoped<ISessionMarker, SessionMarker>();
            builder.Services.AddSingleton<IAppMarker, AppMarker>();
            return builder.Build();
        }));

        foreach (var global in globals) runner.Suite.AddGlobalAction(global);

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
    public async Task global_action_runs_once_around_the_whole_run()
    {
        LifecycleFixture.Reset();

        var action = new RecordingGlobalAction();
        var results = await buildRunner(action).RunAll();

        shouldHaveNoFailures(results);

        action.SetUpCount.ShouldBe(1);
        action.TearDownCount.ShouldBe(1);

        // SetUp lands before the first feature hook, TearDown after the last one.
        action.BeforeAllCountAtSetUp.ShouldBe(0);
        action.AfterAllCountAtTearDown.ShouldBe(1);
    }

    [Fact]
    public async Task global_actions_set_up_in_order_and_tear_down_in_reverse()
    {
        var log = new List<string>();
        var suite = new TestSuite();
        suite.AddGlobalAction(new OrderedGlobalAction("first", log));
        suite.AddGlobalAction(new OrderedGlobalAction("second", log));

        await suite.RunGlobalSetUp();
        await suite.RunGlobalTearDown();

        log.ShouldBe(["first:setup", "second:setup", "second:teardown", "first:teardown"]);
    }

    [Fact]
    public async Task global_action_setup_failure_is_catastrophic()
    {
        var suite = new TestSuite();
        suite.AddGlobalAction(new ThrowingGlobalAction());

        var ex = await Should.ThrowAsync<SpecCatastrophicException>(suite.RunGlobalSetUp());
        ex.Message.ShouldContain("ThrowingGlobalAction");
    }

    private class RecordingGlobalAction : IGlobalAction
    {
        public int SetUpCount;
        public int TearDownCount;
        public int BeforeAllCountAtSetUp;
        public int AfterAllCountAtTearDown;

        public Task SetUp()
        {
            SetUpCount++;
            BeforeAllCountAtSetUp = LifecycleFixture.BeforeAllCount;
            return Task.CompletedTask;
        }

        public Task TearDown()
        {
            TearDownCount++;
            AfterAllCountAtTearDown = LifecycleFixture.AfterAllCount;
            return Task.CompletedTask;
        }
    }

    private class OrderedGlobalAction : IGlobalAction
    {
        private readonly string _name;
        private readonly List<string> _log;

        public OrderedGlobalAction(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public Task SetUp()
        {
            _log.Add($"{_name}:setup");
            return Task.CompletedTask;
        }

        public Task TearDown()
        {
            _log.Add($"{_name}:teardown");
            return Task.CompletedTask;
        }
    }

    private class ThrowingGlobalAction : IGlobalAction
    {
        public Task SetUp() => throw new InvalidOperationException("seed data unavailable");
        public Task TearDown() => Task.CompletedTask;
    }
}
