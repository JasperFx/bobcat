using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

public class PreflightTests
{
    [Fact]
    public async Task an_empty_preflight_succeeds()
    {
        var preflight = new Preflight();

        preflight.IsEmpty.ShouldBeTrue();
        (await preflight.Run()).Succeeded().ShouldBeTrue();
    }

    [Fact]
    public async Task a_check_that_returns_has_passed()
    {
        // The "throw to fail" contract, matching JasperFx's own checks so a resource can satisfy
        // both without adapting.
        var preflight = new Preflight().Add("docker is up", () => { });

        var results = await preflight.Run();

        results.Succeeded().ShouldBeTrue();
        results.Successes.ShouldBe(["docker is up"]);
    }

    [Fact]
    public async Task every_check_runs_even_after_one_fails()
    {
        // The point of a preflight is to report everything that is wrong in one go — stopping at
        // the first failure would mean fixing the environment one round-trip at a time.
        var third = false;

        var results = await new Preflight()
            .Add("first", () => throw new InvalidOperationException("no docker"))
            .Add("second", () => throw new InvalidOperationException("no browsers"))
            .Add("third", () => { third = true; })
            .Run();

        third.ShouldBeTrue();
        results.Failures.Length.ShouldBe(2);
        results.Successes.ShouldBe(["third"]);
    }

    [Fact]
    public async Task the_failure_description_names_every_broken_check_and_why()
    {
        var results = await new Preflight()
            .Add("docker is running", () => throw new InvalidOperationException("connection refused"))
            .Add("database is reachable", () => { })
            .Run();

        var description = Preflight.Describe(results);

        description.ShouldContain("docker is running");
        description.ShouldContain("connection refused");
        description.ShouldContain("1 of 2 checks");
    }

    [Fact]
    public async Task resource_checks_are_added_from_registered_resources()
    {
        var healthy = new StubResource("database");
        var broken = new StubResource("rabbit", () => throw new InvalidOperationException("broker down"));

        var results = await new Preflight().AddResourceChecks([healthy, broken]).Run();

        results.Successes.ShouldBe(["Resource 'database'"]);
        results.Failures.ShouldHaveSingleItem().Description.ShouldBe("Resource 'rabbit'");
    }

    [Fact]
    public async Task a_resource_that_does_not_override_check_passes_by_default()
    {
        // Check is a default interface method, so existing resources are unaffected.
        var results = await new Preflight().AddResourceChecks([new StubResource("legacy")]).Run();

        results.Succeeded().ShouldBeTrue();
    }

    [Fact]
    public async Task a_failing_preflight_aborts_the_run_before_any_feature_executes()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.Preflight.Add("docker is running", () => throw new InvalidOperationException("connection refused"));
        runner.AddFeature(new FeatureDefinition("Never Runs", typeof(NeverRunsFixture),
        [
            new ScenarioDefinition("should not execute", [], (_, _) =>
                throw new Exception("the preflight should have stopped this"))
        ]));

        var results = await runner.RunAll();

        results.PreflightFailure.ShouldNotBeNull();
        results.PreflightFailure.ShouldContain("connection refused");
        results.Features.ShouldBeEmpty();

        // A broken harness is not the same fact as failing tests.
        results.ExitCode.ShouldBe(2);
    }

    public class NeverRunsFixture : Fixture;

    private sealed class StubResource(string name, Action? onCheck = null) : ITestResource
    {
        public string Name { get; } = name;
        public Task Start() => Task.CompletedTask;
        public Task ResetBetweenScenarios() => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;

        public Task Check(CancellationToken token)
        {
            onCheck?.Invoke();
            return Task.CompletedTask;
        }
    }
}
