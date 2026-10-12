using JasperFx.Events.EventModeling;
using Microsoft.CodeAnalysis;
using Shouldly;
using static Bobcat.Generators.Tests.ProjectedEventModelRoleTests;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#500: the route an HTTP act's <c>[EventModelTrigger]</c> puts on its slice — a constant, a route name
/// resolved against the endpoint attributes the compilation can see, or an interpolated route matched against
/// those endpoints (and rebuilt from the interpolation when no single endpoint matches). A route that cannot be
/// known is BOBCAT038, never silence.
/// </summary>
/// <remarks>
/// The endpoint attributes are stand-ins with the real metadata names — <c>Wolverine.Http.WolverinePostAttribute</c>,
/// <c>Microsoft.AspNetCore.Mvc.HttpPostAttribute</c> — because the generator matches them by shape and name, and
/// takes no dependency on either package.
/// </remarks>
public class TriggerRouteTests
{
    private const string Usings =
        """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Bobcat;
        using JasperFx.Events.EventModeling;
        using Bank;
        using Vocab;
        using Xunit;

        """;

    private const string Domain =
        """
        namespace Bank
        {
            public record Deposit(Guid AccountId, decimal Amount);
            public record Withdraw(Guid AccountId, decimal Amount);
            public record CloseAccount(Guid AccountId);
            public class Account { public Guid Id { get; set; } }
        }

        """;

    private const string Vocabulary =
        """
        namespace Vocab
        {
            public abstract class ScenarioBase
            {
                [EventModelRole(SliceRole.Command, nameof(command))]
                [EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "POST")]
                public Task WhenPosted(object command, string route, params object[] routeValues) => Task.CompletedTask;

                // No verb declared: a matched endpoint's own verb is taken
                [EventModelRole(SliceRole.Command, nameof(command))]
                [EventModelTrigger(TriggerKind.Http, Route = nameof(route))]
                public Task WhenSent(object command, string route) => Task.CompletedTask;

                [EventModelRole(SliceRole.Command, nameof(command))]
                [EventModelTrigger(TriggerKind.Http, RouteName = nameof(routeName), HttpMethod = "POST")]
                public Task WhenPostedTo(object command, string routeName, params object[] routeValues) => Task.CompletedTask;

                protected static string RouteFor(Guid id) => "/api/accounts/" + id + "/deposits";
            }
        }

        """;

    /// <summary>Wolverine.Http's attribute family, as far as the generator reads it.</summary>
    private const string WolverineHttp =
        """
        namespace Wolverine.Http
        {
            [AttributeUsage(AttributeTargets.Method)]
            public abstract class WolverineHttpMethodAttribute : Attribute
            {
                protected WolverineHttpMethodAttribute(string httpMethod, string template) { }
                public string? RouteName { get; set; }
                public string? Name { get; set; }
            }

            public class WolverineGetAttribute : WolverineHttpMethodAttribute
            {
                public WolverineGetAttribute(string template) : base("GET", template) { }
            }

            public class WolverinePostAttribute : WolverineHttpMethodAttribute
            {
                public WolverinePostAttribute(string template) : base("POST", template) { }
            }

            public class WolverineDeleteAttribute : WolverineHttpMethodAttribute
            {
                public WolverineDeleteAttribute(string template) : base("DELETE", template) { }
            }

            [AttributeUsage(AttributeTargets.Class)]
            public class RoutePrefixAttribute : Attribute
            {
                public RoutePrefixAttribute(string prefix) { }
            }
        }

        """;

    /// <summary>ASP.NET Core MVC's routing attributes, as far as the generator reads them.</summary>
    private const string Mvc =
        """
        namespace Microsoft.AspNetCore.Mvc.Routing
        {
            public abstract class HttpMethodAttribute : Attribute
            {
                protected HttpMethodAttribute(IEnumerable<string> httpMethods, string? template) { }
                public string? Name { get; set; }
            }
        }

        namespace Microsoft.AspNetCore.Mvc
        {
            public class HttpPostAttribute : Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute
            {
                public HttpPostAttribute(string template) : base(new[] { "POST" }, template) { }
            }

            public class HttpGetAttribute : Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute
            {
                public HttpGetAttribute(string template) : base(new[] { "GET" }, template) { }
            }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = true)]
            public class RouteAttribute : Attribute
            {
                public RouteAttribute(string template) { }
                public string? Name { get; set; }
            }

            public abstract class ControllerBase { }
        }

        """;

    // ---------------------------------------------------------------- a constant route

    [Fact]
    public void a_constant_route_is_stamped_as_written()
    {
        var outcome = run(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit()
                {
                    var id = Guid.NewGuid();
                    await WhenPosted(new Deposit(id, 10m), "/api/accounts/{accountId}/deposits", id);
                }
            }
            """);

        var slice = sliceIn(outcome, "Deposit");
        slice.TriggerOrigin!.HttpRoute.ShouldBe("/api/accounts/{accountId}/deposits");
        slice.TriggerOrigin.HttpMethod.ShouldBe("POST");
        outcome.WithId("BOBCAT038").ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- a route name

    [Fact]
    public void a_route_name_resolves_to_a_wolverine_endpoint_in_the_same_compilation()
    {
        var outcome = run(
            WolverineHttp +
            """
            namespace BankApp
            {
                [Wolverine.Http.RoutePrefix("api")]
                public static class DepositEndpoint
                {
                    [Wolverine.Http.WolverinePost("/accounts/{accountId}/deposits", RouteName = "MakeDeposit", Name = "not the route name")]
                    public static void Post(Deposit command) { }
                }
            }

            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit()
                {
                    var id = Guid.NewGuid();
                    await WhenPostedTo(new Deposit(id, 10m), "MakeDeposit", id);
                }
            }
            """);

        // The class's [RoutePrefix] is applied as Wolverine applies it
        var slice = sliceIn(outcome, "Deposit");
        slice.TriggerOrigin!.HttpRoute.ShouldBe("/api/accounts/{accountId}/deposits");
        slice.TriggerOrigin.HttpMethod.ShouldBe("POST");
        slice.TriggerOrigin.Label.ShouldBe("POST /api/accounts/{accountId}/deposits");
        outcome.WithId("BOBCAT038").ShouldBeEmpty();
    }

    [Fact]
    public void a_route_name_resolves_to_an_endpoint_in_a_referenced_assemblys_metadata()
    {
        // Wolverine.Http and the application each compiled into an assembly of their own: the endpoint is only
        // metadata to the spec project, which is where a real suite's application sits
        var wolverine = GeneratorHarness.CompileLibrary("Wolverine.Http", "using System;\n" + WolverineHttp);
        var wolverineReference = MetadataReference.CreateFromImage(wolverine);
        var app = GeneratorHarness.CompileLibrary("BankApp",
            "using System;\n" + Domain +
            """
            namespace BankApp
            {
                public static class WithdrawEndpoint
                {
                    [Wolverine.Http.WolverinePost("/api/accounts/{accountId}/withdrawals", RouteName = "Withdraw")]
                    public static void Post(Bank.Withdraw command) { }
                }
            }
            """,
            wolverineReference);

        var outcome = GeneratorHarness.Run(
            Usings + Vocabulary +
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Withdraw))]
                public async Task a_withdrawal()
                {
                    var id = Guid.NewGuid();
                    await WhenPostedTo(new Withdraw(id, 5m), "Withdraw", id);
                }
            }
            """,
            options: null,
            extraReferences: [wolverineReference, MetadataReference.CreateFromImage(app)]);

        outcome.CompilationErrors.ShouldBeEmpty();
        outcome.WithId("BOBCAT038").ShouldBeEmpty();

        var slice = GeneratedEventModel.For(load(outcome, wolverine, app))!.Slices.Single(x => x.Name == "Withdraw");
        slice.TriggerOrigin!.HttpRoute.ShouldBe("/api/accounts/{accountId}/withdrawals");
        slice.TriggerOrigin.HttpMethod.ShouldBe("POST");
    }

    [Fact]
    public void a_route_name_resolves_to_an_mvc_action_under_its_controllers_route()
    {
        var outcome = run(
            Mvc +
            """
            namespace BankApp
            {
                [Microsoft.AspNetCore.Mvc.Route("api/[controller]")]
                public class AccountsController : Microsoft.AspNetCore.Mvc.ControllerBase
                {
                    [Microsoft.AspNetCore.Mvc.HttpPost("{accountId}/deposits", Name = "MakeDeposit")]
                    public void MakeDeposit(Guid accountId, Deposit command) { }

                    // An absolute template stands alone, whatever the controller's route
                    [Microsoft.AspNetCore.Mvc.Route("/closures/{accountId}", Name = "Close")]
                    public void Close(Guid accountId) { }
                }
            }

            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit() => await WhenPostedTo(new Deposit(Guid.Empty, 10m), "MakeDeposit", Guid.Empty);

                [Fact, BobcatSlice(SliceType = typeof(CloseAccount))]
                public async Task a_closure() => await WhenPostedTo(new CloseAccount(Guid.Empty), "Close", Guid.Empty);
            }
            """);

        sliceIn(outcome, "Deposit").TriggerOrigin!.HttpRoute.ShouldBe("/api/Accounts/{accountId}/deposits");
        sliceIn(outcome, "CloseAccount").TriggerOrigin!.HttpRoute.ShouldBe("/closures/{accountId}");
        outcome.WithId("BOBCAT038").ShouldBeEmpty();
    }

    [Fact]
    public void a_route_name_no_endpoint_attribute_declares_leaves_the_route_unknown_and_says_so()
    {
        // app.MapPost(...).WithName("MakeDeposit") is a call: only the running host can resolve it
        var outcome = run(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit() => await WhenPostedTo(new Deposit(Guid.Empty, 10m), "MakeDeposit", Guid.Empty);
            }
            """);

        var slice = sliceIn(outcome, "Deposit");
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin.ShouldBeNull();

        var diagnostic = outcome.WithId("BOBCAT038").ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Info);
        diagnostic.GetMessage().ShouldContain("\"MakeDeposit\"");
        diagnostic.GetMessage().ShouldContain(".WithName");
    }

    // ---------------------------------------------------------------- an interpolated route

    [Fact]
    public void an_interpolated_route_takes_the_one_matching_endpoints_template_and_parameter_names()
    {
        var outcome = run(
            WolverineHttp +
            """
            namespace BankApp
            {
                public static class Endpoints
                {
                    [Wolverine.Http.WolverinePost("/api/accounts/{accountId:guid}/deposits")]
                    public static void Deposit(Deposit command) { }

                    [Wolverine.Http.WolverineGet("/api/accounts/{accountId}/statements")]
                    public static void Statement() { }

                    [Wolverine.Http.WolverineDelete("/api/accounts/{accountId}")]
                    public static void Close(CloseAccount command) { }
                }
            }

            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit()
                {
                    var theAccount = Guid.NewGuid();
                    await WhenPosted(new Deposit(theAccount, 10m), $"/api/accounts/{theAccount}/deposits?notify=true");
                }

                [Fact, BobcatSlice(SliceType = typeof(CloseAccount))]
                public async Task a_closure()
                {
                    var account = new Account { Id = Guid.NewGuid() };
                    await WhenSent(new CloseAccount(account.Id), "/API/accounts/" + account.Id);
                }
            }
            """);

        // The endpoint's own template, constraint included -- not {theAccount}, and not the query string
        var deposit = sliceIn(outcome, "Deposit");
        deposit.TriggerOrigin!.HttpRoute.ShouldBe("/api/accounts/{accountId:guid}/deposits");
        deposit.TriggerOrigin.HttpMethod.ShouldBe("POST");

        // A concatenation reads the same way, literals compare case-insensitively, and an act that declares no
        // verb takes the endpoint's
        var close = sliceIn(outcome, "CloseAccount");
        close.TriggerOrigin!.HttpRoute.ShouldBe("/api/accounts/{accountId}");
        close.TriggerOrigin.HttpMethod.ShouldBe("DELETE");

        outcome.WithId("BOBCAT038").ShouldBeEmpty();
    }

    [Fact]
    public void an_interpolated_route_with_no_matching_endpoint_is_rebuilt_from_its_holes()
    {
        var outcome = run(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit()
                {
                    var theAccount = new Account { Id = Guid.NewGuid() };
                    var branch = "north";
                    await WhenPosted(new Deposit(theAccount.Id, 10m),
                        $"/branches/{branch}/accounts/{theAccount.Id}/deposits/{Guid.NewGuid()}/{Guid.NewGuid():N}");
                }
            }
            """);

        // A simple identifier is its own name, a member access its last member, anything else {value} --
        // numbered when it repeats, so the template stays a valid one
        sliceIn(outcome, "Deposit").TriggerOrigin!.HttpRoute
            .ShouldBe("/branches/{branch}/accounts/{Id}/deposits/{value}/{value2}");
        outcome.WithId("BOBCAT038").ShouldBeEmpty();
    }

    [Fact]
    public void an_interpolated_route_matching_several_endpoint_templates_picks_none_of_them()
    {
        var outcome = run(
            WolverineHttp +
            """
            namespace BankApp
            {
                public static class Endpoints
                {
                    [Wolverine.Http.WolverinePost("/api/accounts/{accountId}/deposits")]
                    public static void Deposit(Deposit command) { }

                    [Wolverine.Http.WolverinePost("/api/accounts/{id}/deposits")]
                    public static void DepositAgain(Deposit command) { }

                    // Same shape, other verb: a POST act never takes it
                    [Wolverine.Http.WolverineGet("/api/ledgers/{ledgerId}/entries")]
                    public static void Entries() { }

                    [Wolverine.Http.WolverinePost("/api/ledgers/{ledger}/entries")]
                    public static void AddEntry(Withdraw command) { }
                }
            }

            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit()
                {
                    var theAccount = Guid.NewGuid();
                    await WhenPosted(new Deposit(theAccount, 10m), $"/api/accounts/{theAccount}/deposits");
                }

                [Fact, BobcatSlice(SliceType = typeof(Withdraw))]
                public async Task an_entry()
                {
                    var theLedger = Guid.NewGuid();
                    await WhenPosted(new Withdraw(theLedger, 1m), $"/api/ledgers/{theLedger}/entries");
                }
            }
            """);

        // Two templates fit: neither is chosen, the test's own names are used instead
        sliceIn(outcome, "Deposit").TriggerOrigin!.HttpRoute.ShouldBe("/api/accounts/{theAccount}/deposits");

        // The verb settles the second: only the POST endpoint is a candidate
        sliceIn(outcome, "Withdraw").TriggerOrigin!.HttpRoute.ShouldBe("/api/ledgers/{ledger}/entries");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void a_read_route_beats_a_rebuilt_one_whichever_specification_folds_first(bool readFirst)
    {
        var rebuilt =
            """
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task rebuilt() { var theAccount = Guid.NewGuid(); await WhenPosted(new Deposit(theAccount, 1m), $"/accounts/{theAccount}"); }

            """;
        var read =
            """
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task read() => await WhenPosted(new Deposit(Guid.Empty, 1m), "/accounts/{accountId}", Guid.Empty);

            """;

        var outcome = run(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
            """ + (readFirst ? read + rebuilt : rebuilt + read) + "}");

        sliceIn(outcome, "Deposit").TriggerOrigin!.HttpRoute.ShouldBe("/accounts/{accountId}");
    }

    // ---------------------------------------------------------------- BOBCAT038 and BOBCAT034

    [Fact]
    public void a_route_from_a_helper_call_is_unknown_and_reported_as_info()
    {
        var outcome = run(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit()
                {
                    var id = Guid.NewGuid();
                    await WhenPosted(new Deposit(id, 10m), RouteFor(id));
                }

                // An HTTP call that only arranges is not the act, so its route is nobody's business
                [Fact, BobcatSlice(SliceType = typeof(Withdraw))]
                public async Task a_withdrawal()
                {
                    var id = Guid.NewGuid();
                    await WhenPosted(new Deposit(id, 10m), RouteFor(id));
                    await WhenPosted(new Withdraw(id, 1m), "/withdrawals");
                }
            }
            """);

        var slice = sliceIn(outcome, "Deposit");
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin.ShouldBeNull();

        var diagnostic = outcome.WithId("BOBCAT038").ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Info);
        diagnostic.GetMessage().ShouldContain("'WhenPosted' is this specification's HTTP act");
        diagnostic.GetMessage().ShouldContain("'route'");
        diagnostic.Location.SourceTree!.ToString().Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)
            .ShouldBe("RouteFor(id)");
    }

    [Fact]
    public void a_route_name_naming_no_parameter_is_bobcat034()
    {
        var outcome = GeneratorHarness.Run(
            Usings + Domain +
            """
            namespace Vocab
            {
                public abstract class Broken
                {
                    [EventModelRole(SliceRole.Command, nameof(command))]
                    [EventModelTrigger(TriggerKind.Http, RouteName = "name")]
                    public Task WhenPostedTo(object command, string routeName) => Task.CompletedTask;
                }
            }
            """);

        outcome.WithId("BOBCAT034").Select(x => x.GetMessage()).ShouldHaveSingleItem()
            .ShouldContain("RouteName = \"name\"");
    }

    // ---------------------------------------------------------------- helpers

    private static GeneratorHarness.RunOutcome run(string specs)
    {
        var outcome = GeneratorHarness.Run(Usings + Domain + Vocabulary + specs);
        outcome.CompilationErrors.ShouldBeEmpty();
        return outcome;
    }

    private static EventModelSliceDescriptor sliceIn(GeneratorHarness.RunOutcome outcome, string name)
        => GeneratedEventModel.For(load(outcome))!.Slices.Single(x => x.Name == name);
}
