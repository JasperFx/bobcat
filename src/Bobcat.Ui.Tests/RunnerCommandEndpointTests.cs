using System.Text.Json;
using Bobcat.Ui.Runners;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace Bobcat.Ui.Tests;

/// <summary>
/// The command endpoint resolves what a person asked for into identities the runner registered —
/// a feature, and since bobcat#645 the whole project — so the runner only ever receives identities.
/// </summary>
public class RunnerCommandEndpointTests
{
    private static RunnerRegistry registry(params string[] specs)
    {
        var runners = new RunnerRegistry();
        runners.Register(new RunnerRegistered("r-1", "/code/bank", "main", "Bank.Tests", "projected", ["cold", "warm"],
            specs.Length > 0 ? specs : ["F/a", "F/b", "G/c"]), "JasperFx/bank");
        return runners;
    }

    private static object? valueOf(IResult result) => (result as IValueHttpResult)?.Value;

    private static string errorOf(IResult result)
        => JsonSerializer.Serialize(valueOf(result));

    [Fact]
    public void all_runs_every_specification_the_runner_registered()
    {
        var runners = registry();

        var result = RunnerEndpoints.Ask("r-1", new RunnerCommandRequest(All: true), runners);

        var queued = valueOf(result).ShouldBeOfType<RunnerCommandQueued>();
        queued.Specs.ShouldBe(["F/a", "F/b", "G/c"]);
        queued.Mode.ShouldBe(RunnerWire.Cold);
        runners.Find("r-1")!.Commands.Single().Specs.ShouldBe(["F/a", "F/b", "G/c"]);
    }

    [Fact]
    public void all_is_one_source_and_cannot_be_combined_with_another()
    {
        var result = RunnerEndpoints.Ask("r-1", new RunnerCommandRequest(Feature: "F", All: true), registry());

        (result as IStatusCodeHttpResult)!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        errorOf(result).ShouldContain("name exactly one of specs, feature or all");
    }

    [Fact]
    public void all_on_a_runner_that_registered_nothing_is_refused_rather_than_sent_empty()
    {
        var runners = new RunnerRegistry();
        runners.Register(new RunnerRegistered("r-1", "/code/bank", "main", "Bank.Tests", "projected", null, []), null);

        var result = RunnerEndpoints.Ask("r-1", new RunnerCommandRequest(All: true), runners);

        errorOf(result).ShouldContain("registered no specifications");
        runners.Find("r-1")!.Commands.ShouldBeEmpty();
    }
}

/// <summary>
/// bobcat#644: a runner's specifications, with the planned steps it registered, are what the runner
/// page lists before anything has run.
/// </summary>
public class RunnerSpecificationsTests
{
    [Fact]
    public void each_spec_is_listed_by_feature_with_the_steps_its_runner_registered()
    {
        var runners = new RunnerRegistry();
        runners.Register(new RunnerRegistered("r-1", "/code/bank", "main", "Bank.Tests", "projected", null,
            ["Withdrawals/an overdraft is refused", "Accounts/an account is opened", "Accounts/a row (amount: 5)"],
            [new RunnerSpecOutline("Accounts/an account is opened", ["Given a customer", "When they open an account"])]), null);

        var specs = RunnerEndpoints.Specifications("r-1", runners) as Microsoft.AspNetCore.Http.IValueHttpResult;
        var listed = specs!.Value.ShouldBeAssignableTo<IReadOnlyList<RunnerSpecView>>()!;

        listed.Select(x => x.Identity).ShouldBe(
            ["Accounts/a row (amount: 5)", "Accounts/an account is opened", "Withdrawals/an overdraft is refused"]);
        listed[1].Feature.ShouldBe("Accounts");
        listed[1].Scenario.ShouldBe("an account is opened");
        listed[1].Steps.ShouldBe(["Given a customer", "When they open an account"]);

        // A runner that predates the outline still lists what it can run
        listed[2].Steps.ShouldBeEmpty();
    }

    [Fact]
    public void an_unknown_runner_is_a_404()
    {
        (RunnerEndpoints.Specifications("nobody", new RunnerRegistry()) as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)!
            .StatusCode.ShouldBe(404);
    }
}
