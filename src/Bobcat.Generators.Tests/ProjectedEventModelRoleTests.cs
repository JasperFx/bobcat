using System.Reflection;
using System.Runtime.Loader;
using JasperFx.Events.EventModeling;
using Microsoft.CodeAnalysis;
using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#462 / #463: a projected specification reaches the Event Model with the roles its body states
/// through role-stamped vocabulary — <c>[EventModelRole]</c> / <c>[EventModelTrigger]</c> on the scenario
/// base's methods, read from the resolved symbol — exactly as a <c>.feature</c> file's type captures do.
/// </summary>
/// <remarks>
/// <c>Vocabulary</c> is a stand-in for <c>WolverineScenario</c>: the same method shapes (a generic
/// aggregate, <c>params</c> events, an <c>object</c> command, <c>typeof</c> event lists, generic read
/// models and messages, an HTTP act with a route), declared in the test source so the generator has
/// nothing but the attributes to go on. One test compiles it into an assembly of its own, which is the
/// package boundary WolverineFx.Bobcat sits across.
/// </remarks>
public class ProjectedEventModelRoleTests
{
    // One block of usings for every compilation here: C# wants them ahead of any namespace, and the specs
    // are concatenated after the domain and the vocabulary. JasperFx.Events.EventModeling is in on purpose:
    // it has an EventModelRole ENUM, and the attribute must still bind as [EventModelRole(...)] beside it.
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
            public record OpenAccount(Guid Id, string Owner);
            public record Deposit(Guid Id, decimal Amount);
            public record AccountOpened(Guid Id, string Owner);
            public record FundsDeposited(Guid Id, decimal Amount);
            public record DepositReceipt(Guid Id);
            public class Account { public Guid Id { get; set; } }
            public class AccountSummary { public Guid Id { get; set; } }
            public class Statement { public Guid Id { get; set; } }
            public record ClientEnrolled(Guid Id, string Name);
            public class Client { public Guid Id { get; set; } }
        }

        """;

    private const string Vocabulary =
        """
        namespace Vocab
        {
            public abstract class ScenarioBase
            {
                [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
                [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
                public Task GivenEvents<TAggregate>(Guid id, params object[] events) where TAggregate : class => Task.CompletedTask;

                [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
                public Task GivenNoEventsFor<TAggregate>(Guid id) where TAggregate : class => Task.CompletedTask;

                [EventModelRole(SliceRole.Context, nameof(TContext))]
                [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
                public Task GivenContext<TContext>(Guid id, params object[] events) where TContext : class => Task.CompletedTask;

                [EventModelRole(SliceRole.Context, nameof(context))]
                public Task GivenContext(Type context, Guid id) => Task.CompletedTask;

                [EventModelRole(SliceRole.ReadModel, nameof(T))]
                public Task GivenReadModel<T>(object document) where T : class => Task.CompletedTask;

                [EventModelRole(SliceRole.Command, nameof(message))]
                public Task WhenReceived(object message) => Task.CompletedTask;

                [EventModelRole(SliceRole.Command, nameof(command))]
                [EventModelTrigger(TriggerKind.Http, Route = nameof(route))]
                public Task WhenPosted(object command, string route) => Task.CompletedTask;

                // bobcat#501: a read-only act -- a GET/HEAD trigger plus a read role, no command
                [EventModelRole(SliceRole.ReadModel, nameof(T))]
                [EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "GET")]
                public Task WhenReading<T>(string route) where T : class => Task.CompletedTask;

                [EventModelRole(SliceRole.ReadModel, nameof(T))]
                [EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "HEAD")]
                public Task WhenProbing<T>(string route) where T : class => Task.CompletedTask;

                // The same read role behind a POST is no read act: an unsafe verb is not a query
                [EventModelRole(SliceRole.ReadModel, nameof(T))]
                [EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "POST")]
                public Task WhenSearching<T>(string route) where T : class => Task.CompletedTask;

                [EventModelRole(SliceRole.Event, nameof(events))]
                public void ThenEvents(params Type[] events) { }

                [EventModelRole(SliceRole.Event, nameof(events))]
                public void ThenEvents(params object[] events) { }

                [EventModelRole(SliceRole.Event, nameof(T))]
                public void ThenEmitted<T>() { }

                [EventModelRole(SliceRole.ReadModel, nameof(T))]
                public Task<T> ThenReadModel<T>(object id) where T : class => Task.FromResult<T>(null!);

                [EventModelRole(SliceRole.Document, nameof(T))]
                public Task<T> ThenDocument<T>(object id) where T : class => Task.FromResult<T>(null!);

                [EventModelRole(SliceRole.Message, nameof(T))]
                public void ThenMessageSent<T>() { }

                // No role: a stream version or a refusal is not an element on the canvas
                public void ThenRefusedWith(string reason) { }

                protected static Specified<T> Specify<T>() => Specifications.Specify<T>();
            }
        }

        """;

    // ---------------------------------------------------------------- the roles each shape stamps

    [Fact]
    public void a_command_slice_states_every_role()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit_is_credited()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));
                    await WhenReceived(new Deposit(id, 10m));
                    ThenEvents(typeof(FundsDeposited));
                    ThenMessageSent<DepositReceipt>();
                }
            }
            """).Single(x => x.Name == "Deposit");

        slice.CommandType!.Name.ShouldBe("Deposit");
        names(slice.AggregateTypes).ShouldBe(["Account"]);
        names(slice.EmittedEvents).ShouldBe(["FundsDeposited"]);
        names(slice.PublishedMessages).ShouldBe(["DepositReceipt"]);
        slice.Pattern.ShouldBe(SlicePattern.Command);

        // #297: on a Command slice the arranged event is the aggregate's own history, not consumed
        slice.ConsumedEvents.ShouldBeEmpty();

        // Every type the body named, in call order — #107's join target, empty in this arm until now
        var spec = slice.Specifications.ShouldHaveSingleItem();
        spec.Identity.ShouldBe("Accounts/a deposit is credited");
        names(spec.ResolvedTypes).ShouldBe(["Account", "AccountOpened", "Deposit", "FundsDeposited", "DepositReceipt"]);
    }

    [Fact]
    public void an_arranged_lookup_stream_is_read_from_and_never_an_aggregate()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(OpenAccount))]
                public async Task an_account_is_opened_for_an_enrolled_client()
                {
                    var client = Guid.NewGuid();
                    await GivenContext<Client>(client, new ClientEnrolled(client, "Ann"));
                    await WhenReceived(new OpenAccount(Guid.NewGuid(), "Ann"));
                    ThenEvents(typeof(AccountOpened));
                }
            }
            """).Single(x => x.Name == "OpenAccount");

        // bobcat#502: the slice reads Client before deciding; it does not decide against it
        names(slice.ReadsFrom).ShouldBe(["Client"]);
        slice.AggregateTypes.ShouldBeEmpty();
        slice.Pattern.ShouldBe(SlicePattern.Command);

        // Its events are arranged history, which a Command slice does not consume (#297)
        slice.ConsumedEvents.ShouldBeEmpty();

        // Still something the specification touched
        names(slice.Specifications.Single().ResolvedTypes).ShouldBe(["Client", "ClientEnrolled", "OpenAccount", "AccountOpened"]);
    }

    [Fact]
    public void a_context_beside_the_decided_stream_keeps_the_two_apart_and_reads_typeof_too()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit_checks_the_client()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));
                    await GivenContext(typeof(Client), Guid.NewGuid());
                    await WhenReceived(new Deposit(id, 10m));
                }

                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_second_spec_naming_the_same_context_adds_nothing()
                {
                    await GivenContext<Client>(Guid.NewGuid());
                    await WhenReceived(new Deposit(Guid.NewGuid(), 1m));
                }
            }
            """).Single(x => x.Name == "Deposit");

        names(slice.AggregateTypes).ShouldBe(["Account"]);
        names(slice.ReadsFrom).ShouldBe(["Client"]);
    }

    [Fact]
    public void a_slice_with_no_context_says_nothing_it_reads_from()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));
                    await WhenReceived(new Deposit(id, 10m));
                }
            }
            """).Single(x => x.Name == "Deposit");

        slice.ReadsFrom.ShouldBeEmpty();
    }

    [Fact]
    public void the_act_is_the_last_command_and_earlier_ones_arrange()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit_after_opening_by_command()
                {
                    var id = Guid.NewGuid();
                    await WhenReceived(new OpenAccount(id, "Ann"));
                    await WhenReceived(Specify<Deposit>().With(x => x.Amount, 10m));
                    ThenEmitted<FundsDeposited>();
                }
            }
            """).Single(x => x.Name == "Deposit");

        // OpenWallet-for-CreditWallet was this mistake in the Gherkin lane; the last When is the act
        slice.CommandType!.Name.ShouldBe("Deposit");
        names(slice.EmittedEvents).ShouldBe(["FundsDeposited"]);
        names(slice.Specifications.Single().ResolvedTypes).ShouldBe(["OpenAccount", "Deposit", "FundsDeposited"]);
    }

    [Fact]
    public void a_view_slice_consumes_what_its_scenarios_arranged()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            [BobcatSlice(SliceType = typeof(AccountSummary), Domain = "Banking")]
            public class account_summary : ScenarioBase
            {
                [Fact]
                public async Task the_summary_shows_the_balance()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new AccountOpened(id, "Ann"), new FundsDeposited(id, 10m));
                    await ThenReadModel<AccountSummary>(id);
                }

                [Fact]
                public async Task a_statement_is_a_document_and_stamps_no_slot()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new[] { new AccountOpened(id, "Ann") });
                    await ThenReadModel<AccountSummary>(id);
                    await ThenDocument<Statement>(id);
                }
            }
            """).Single(x => x.Name == "AccountSummary");

        slice.Pattern.ShouldBe(SlicePattern.View);
        slice.Domain.ShouldBe("Banking");
        names(slice.ReadModelTypes).ShouldBe(["AccountSummary"]);
        names(slice.ConsumedEvents).ShouldBe(["AccountOpened", "FundsDeposited"]);
        slice.CommandType.ShouldBeNull();

        // A document is resolved, as {document} is, but is no element of the slice
        names(slice.ReadModelTypes).ShouldNotContain("Statement");
        names(slice.Specifications.Single(x => x.Identity.EndsWith("stamps no slot")).ResolvedTypes)
            .ShouldContain("Statement");
    }

    [Fact]
    public void an_http_act_stamps_its_trigger_kind_and_constant_route()
    {
        var slices = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task posted()
                {
                    await WhenPosted(new Deposit(Guid.Empty, 10m), "/accounts/deposit");
                }

                [Fact, BobcatSlice(SliceType = typeof(OpenAccount))]
                public async Task posted_to_a_computed_route()
                {
                    var id = Guid.NewGuid();
                    await WhenPosted(new OpenAccount(id, "Ann"), routeFor(id));
                }

                private static string routeFor(Guid id) => "/accounts/" + id;
            }
            """);

        var deposit = slices.Single(x => x.Name == "Deposit");
        deposit.TriggerKind.ShouldBe(TriggerKind.Http);
        deposit.TriggerOrigin!.HttpRoute.ShouldBe("/accounts/deposit");
        deposit.TriggerOrigin.HttpMethod.ShouldBe("POST");

        // The kind is certain; a route the generator cannot know is left off rather than guessed (an
        // interpolated route is TriggerRouteTests' business, bobcat#500)
        var open = slices.Single(x => x.Name == "OpenAccount");
        open.TriggerKind.ShouldBe(TriggerKind.Http);
        open.TriggerOrigin.ShouldBeNull();
    }

    [Fact]
    public void an_http_call_that_only_arranges_does_not_make_the_slice_http()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task posted_open_then_received_deposit()
                {
                    await WhenPosted(new OpenAccount(Guid.Empty, "Ann"), "/accounts");
                    await WhenReceived(new Deposit(Guid.Empty, 10m));
                }
            }
            """).Single(x => x.Name == "Deposit");

        slice.CommandType!.Name.ShouldBe("Deposit");
        slice.TriggerKind.ShouldBeNull();
    }

    // ---------------------------------------------------------------- bobcat#501, a read-only act is a Query

    [Fact]
    public void a_get_read_act_is_a_query_slice_and_its_arranged_stream_stamps_nothing()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceName = "GET /accounts/{id}/summary")]
                public async Task the_summary_is_read()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new AccountOpened(id, "Ann"), new FundsDeposited(id, 10m));
                    await WhenReading<AccountSummary>("/accounts/{id}/summary");
                }
            }
            """).Single(x => x.Name == "GET /accounts/{id}/summary");

        slice.Pattern.ShouldBe(SlicePattern.Query);
        slice.CommandType.ShouldBeNull();
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin!.HttpRoute.ShouldBe("/accounts/{id}/summary");
        slice.TriggerOrigin.HttpMethod.ShouldBe("GET");

        // What it reads is ReadsFrom -- the ReadModelRead link's end -- never a read model it produces. The stream
        // arranged with plain GivenEvents is setup feeding the projection: not read, not decided against, its events
        // not consumed. It stamps nothing on the slice
        names(slice.ReadsFrom).ShouldBe(["AccountSummary"]);
        slice.ReadModelTypes.ShouldBeEmpty();
        slice.AggregateTypes.ShouldBeEmpty();
        slice.ConsumedEvents.ShouldBeEmpty();
        slice.PatternViolations.ShouldBeEmpty();

        names(slice.Specifications.Single().ResolvedTypes)
            .ShouldBe(["Account", "AccountOpened", "FundsDeposited", "AccountSummary"]);
    }

    // bobcat#612: a list GET is the commonest query, and its read model is the element type
    [Theory]
    [InlineData("List<AccountSummary>")]
    [InlineData("AccountSummary[]")]
    [InlineData("IReadOnlyList<AccountSummary>")]
    [InlineData("IEnumerable<AccountSummary>")]
    public void a_list_read_act_is_a_query_slice_reading_its_element_type(string readModel)
    {
        var slice = describe(
            $$"""
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceName = "GET /accounts")]
                public async Task the_summaries_are_listed()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));
                    await WhenReading<{{readModel}}>("/accounts");
                }
            }
            """).Single(x => x.Name == "GET /accounts");

        slice.Pattern.ShouldBe(SlicePattern.Query);
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin!.HttpRoute.ShouldBe("/accounts");
        slice.TriggerOrigin.HttpMethod.ShouldBe("GET");
        names(slice.ReadsFrom).ShouldBe(["AccountSummary"]);
        slice.ReadModelTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_dictionary_read_names_no_read_model()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceName = "GET /accounts/by-id")]
                public async Task the_summaries_are_keyed()
                {
                    await WhenReading<Dictionary<Guid, AccountSummary>>("/accounts/by-id");
                }
            }
            """).Single(x => x.Name == "GET /accounts/by-id");

        // A keyed collection is not a list of one thing: it stamps no read role, so no read act either
        slice.ReadsFrom.ShouldBeEmpty();
        slice.Pattern.ShouldNotBe(SlicePattern.Query);
    }

    [Fact]
    public void a_head_read_act_is_a_query_too_and_an_explicit_context_stays_valid()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceName = "Client accounts")]
                public async Task the_accounts_are_probed()
                {
                    var client = Guid.NewGuid();
                    await GivenContext<Client>(client, new ClientEnrolled(client, "Ann"));
                    await GivenEvents<Account>(Guid.NewGuid(), new AccountOpened(Guid.NewGuid(), "Ann"));
                    await WhenProbing<AccountSummary>("/clients/{client}/accounts");
                    await ThenReadModel<Statement>(client);
                }
            }
            """).Single(x => x.Name == "Client accounts");

        // Only the explicit GivenContext, the read type and an asserted read model are read; the GivenEvents<Account>
        // stream is setup
        slice.Pattern.ShouldBe(SlicePattern.Query);
        slice.TriggerOrigin!.HttpMethod.ShouldBe("HEAD");
        names(slice.ReadsFrom).ShouldBe(["Client", "AccountSummary", "Statement"]);
        slice.ReadModelTypes.ShouldBeEmpty();
        slice.AggregateTypes.ShouldBeEmpty();
        slice.ConsumedEvents.ShouldBeEmpty();
    }

    [Fact]
    public void a_post_is_never_a_read_act_and_a_command_still_makes_a_command_slice()
    {
        var slices = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(AccountSummary))]
                public async Task searched()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));
                    await WhenSearching<AccountSummary>("/accounts/search");
                }

                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task posted() => await WhenPosted(new Deposit(Guid.Empty, 10m), "/deposits");
            }
            """);

        // A read role behind a POST is an asserted read model, as before: a View that consumes its arrangement,
        // and no act to take a trigger from
        var searched = slices.Single(x => x.Name == "AccountSummary");
        searched.Pattern.ShouldBe(SlicePattern.View);
        names(searched.ReadModelTypes).ShouldBe(["AccountSummary"]);
        names(searched.ConsumedEvents).ShouldBe(["AccountOpened"]);
        searched.TriggerKind.ShouldBeNull();

        var posted = slices.Single(x => x.Name == "Deposit");
        posted.Pattern.ShouldBe(SlicePattern.Command);
        posted.TriggerOrigin!.HttpMethod.ShouldBe("POST");
    }

    [Fact]
    public void a_declared_pattern_still_wins_over_a_read_act()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(AccountSummary), Pattern = "View")]
                public async Task the_summary_is_read()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));
                    await WhenReading<AccountSummary>("/accounts/{id}/summary");
                }
            }
            """).Single(x => x.Name == "AccountSummary");

        // #323: the model's word settles it, and the slice is the View it was before #501. The GET is the query's
        // trigger, not the view's, so it is left off
        slice.Pattern.ShouldBe(SlicePattern.View);
        names(slice.ReadModelTypes).ShouldBe(["AccountSummary"]);
        names(slice.ConsumedEvents).ShouldBe(["AccountOpened"]);
        names(slice.AggregateTypes).ShouldBe(["Account"]);
        slice.ReadsFrom.ShouldBeEmpty();
        slice.TriggerKind.ShouldBeNull();
    }

    [Fact]
    public void the_last_act_decides_so_a_read_after_an_arranging_command_is_a_query()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceName = "GET /accounts/{id}")]
                public async Task opened_then_read()
                {
                    var id = Guid.NewGuid();
                    await WhenPosted(new OpenAccount(id, "Ann"), "/accounts");
                    await WhenReading<Account>("/accounts/{id}");
                }
            }
            """).Single(x => x.Name == "GET /accounts/{id}");

        // The POST arranged; the GET is the act. The arranging command is no command of a Query
        slice.Pattern.ShouldBe(SlicePattern.Query);
        slice.CommandType.ShouldBeNull();
        slice.TriggerOrigin!.Label.ShouldBe("GET /accounts/{id}");
        names(slice.ReadsFrom).ShouldBe(["Account"]);
        names(slice.Specifications.Single().ResolvedTypes).ShouldBe(["OpenAccount", "Account"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void a_command_act_on_the_same_slice_outranks_a_read_act_whichever_folds_first(bool readFirst)
    {
        const string read = """
                            [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                            public async Task read() => await WhenReading<Account>("/accounts/{id}");
                            """;
        const string command = """
                               [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                               public async Task received() => await WhenReceived(new Deposit(Guid.Empty, 1m));
                               """;

        var slice = describe(
            $$"""
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                {{(readFirst ? read : command)}}
                {{(readFirst ? command : read)}}
            }
            """).Single(x => x.Name == "Deposit");

        slice.Pattern.ShouldBe(SlicePattern.Command);
        slice.CommandType!.Name.ShouldBe("Deposit");

        // The GET is not this slice's trigger in either order
        slice.TriggerKind.ShouldBeNull();
        slice.TriggerOrigin.ShouldBeNull();
    }

    [Fact]
    public void a_query_that_emits_events_is_a_pattern_violation_upstream()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceName = "GET /accounts/{id}")]
                public async Task a_read_that_writes()
                {
                    await WhenReading<Account>("/accounts/{id}");
                    ThenEvents(typeof(FundsDeposited));
                }
            }
            """).Single(x => x.Name == "GET /accounts/{id}");

        // Stamped as stated; JasperFx computes the violation from the roles (jasperfx#1003)
        slice.Pattern.ShouldBe(SlicePattern.Query);
        names(slice.EmittedEvents).ShouldBe(["FundsDeposited"]);
        slice.PatternViolations.ShouldHaveSingleItem().Role.ShouldBe(EventModelRole.EmittedEvents);
    }

    [Fact]
    public void a_jasperfx_without_query_or_route_names_gets_the_inference_it_had_before()
    {
        // The consumer's JasperFx is probed (BobcatGenerator): one older than 2.85.0 has neither member, and the
        // generated source must still compile against it
        var slice = new EventModelEmitter.SliceModel { Name = "GET /accounts/{id}", ClassName = "GET_accounts_id" };
        slice.ReadModels.Add("global::Bank.Account");
        slice.ReadActs.Add("global::Bank.Account");
        slice.TriggerKind = "Http";
        slice.TriggerFromRead = true;
        slice.TriggerHttpMethod = "GET";
        slice.TriggerRouteName = "GetAccount";

        var old = EventModelEmitter.EmitSource("Specs", null, [slice], new EventModelEmitter.Contract());
        old.ShouldNotContain("SlicePattern.Query");
        old.ShouldNotContain("HttpRouteName");
        old.ShouldContain("SlicePattern.View");

        var latest = EventModelEmitter.EmitSource("Specs", null, [slice]);
        latest.ShouldContain("SlicePattern.Query");
        latest.ShouldContain("HttpRouteName = \"GetAccount\"");
    }

    [Fact]
    public void an_unbound_test_with_roles_falls_back_to_the_feature_title()
    {
        var slices = describe(
            """
            [BobcatFeature("Confirming deposits")]
            public class unbound : ScenarioBase
            {
                [Fact]
                public async Task a_deposit()
                {
                    await WhenReceived(new Deposit(Guid.Empty, 10m));
                }

                [Fact]
                public void says_nothing_typed() => ThenRefusedWith("no");
            }
            """);

        var slice = slices.ShouldHaveSingleItem();
        slice.Name.ShouldBe("Confirming deposits");
        slice.CommandType!.Name.ShouldBe("Deposit");

        // A test with neither a binding nor a role contributes nothing, as before
        slice.Specifications.ShouldHaveSingleItem().Identity.ShouldBe("Confirming deposits/a deposit");
    }

    [Fact]
    public void an_argument_of_no_knowable_type_contributes_nothing()
    {
        var slice = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task opaque()
                {
                    object message = new Deposit(Guid.Empty, 1m);
                    await WhenReceived(message);
                    ThenEvents((object)null!);
                }
            }
            """).Single(x => x.Name == "Deposit");

        slice.CommandType.ShouldBeNull();
        slice.EmittedEvents.ShouldBeEmpty();
        slice.Specifications.Single().ResolvedTypes.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- #463: TriggeredBy

    [Fact]
    public void a_methods_triggered_by_wins_over_its_classes_whichever_folds_first()
    {
        const string byTheClass =
            """
                [Fact]
                public async Task by_the_class() => await WhenReceived(new Deposit(Guid.Empty, 1m));

            """;
        const string byTheMethod =
            """
                [Fact, BobcatSlice(TriggeredBy = "the account holder")]
                public async Task by_the_method() => await WhenReceived(new Deposit(Guid.Empty, 2m));

            """;

        // Both orders of the same two tests: the method's label must win either way (#258)
        foreach (var members in new[] { byTheClass + byTheMethod, byTheMethod + byTheClass })
        {
            var slice = describe(
                $$"""
                [BobcatFeature("Accounts")]
                [BobcatSlice(SliceType = typeof(Deposit), TriggeredBy = "the bank clerk")]
                public class accounts : ScenarioBase
                {
                {{members}}
                }
                """).Single(x => x.Name == "Deposit");

            slice.TriggerLabel.ShouldBe("the account holder");

            // A method attribute that sets only the trigger keeps the class's slice: both tests are on it
            slice.Specifications.Count.ShouldBe(2);
        }
    }

    [Fact]
    public void a_class_triggered_by_is_the_fallback_and_does_not_follow_a_rebinding()
    {
        var slices = describe(
            """
            [BobcatFeature("Accounts")]
            [BobcatSlice(SliceType = typeof(Deposit), TriggeredBy = "the account holder")]
            public class accounts : ScenarioBase
            {
                [Fact]
                public async Task deposits() => await WhenReceived(new Deposit(Guid.Empty, 1m));

                [Fact, BobcatSlice(SliceType = typeof(OpenAccount))]
                public async Task opens() => await WhenReceived(new OpenAccount(Guid.Empty, "Ann"));
            }
            """);

        slices.Single(x => x.Name == "Deposit").TriggerLabel.ShouldBe("the account holder");
        slices.Single(x => x.Name == "OpenAccount").TriggerLabel.ShouldBeNull();
    }

    // ---------------------------------------------------------------- the whole reading

    // These two scenarios were once pinned against their Gherkin twins (the arms had to agree). With
    // the Gherkin lane gone (bobcat#471) the expected reading is stated outright instead.
    private const string ProjectedEquivalent =
        """
        [BobcatFeature("Accounts")]
        public class accounts : ScenarioBase
        {
            [Fact, BobcatSlice(SliceType = typeof(Deposit), Domain = "Banking")]
            public async Task a_deposit_after_opening()
            {
                var id = Guid.NewGuid();
                await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));
                await WhenReceived(new OpenAccount(id, "Ann"));
                await WhenReceived(new Deposit(id, 10m));
                ThenEvents(typeof(FundsDeposited));
                ThenMessageSent<DepositReceipt>();
            }

            [Fact, BobcatSlice(SliceType = typeof(AccountSummary), Domain = "Banking")]
            public async Task the_summary_shows_the_balance()
            {
                var id = Guid.NewGuid();
                await GivenEvents<Account>(id, new AccountOpened(id, "Ann"), new FundsDeposited(id, 10m));
                await ThenReadModel<AccountSummary>(id);
            }
        }
        """;

    [Fact]
    public void a_command_slice_and_a_view_slice_read_in_full()
    {
        var projected = describe(ProjectedEquivalent);

        projected.Select(x => x.Name).OrderBy(x => x).ShouldBe(["AccountSummary", "Deposit"]);

        var deposit = projected.Single(x => x.Name == "Deposit");
        deposit.CommandType!.Name.ShouldBe("Deposit");
        deposit.Pattern.ShouldBe(SlicePattern.Command);
        deposit.Domain.ShouldBe("Banking");
        names(deposit.AggregateTypes).ShouldBe(["Account"]);
        names(deposit.EmittedEvents).ShouldBe(["FundsDeposited"]);
        names(deposit.PublishedMessages).ShouldBe(["DepositReceipt"]);
        names(deposit.ReadModelTypes).ShouldBeEmpty();

        // An arranged event is the aggregate's own stream on a Command slice, so it is not consumed (#297)
        deposit.ConsumedEvents.ShouldBeEmpty();

        var summary = projected.Single(x => x.Name == "AccountSummary");
        summary.CommandType.ShouldBeNull();
        summary.Pattern.ShouldBe(SlicePattern.View);
        summary.Domain.ShouldBe("Banking");
        names(summary.ReadModelTypes).ShouldBe(["AccountSummary"]);
        names(summary.ConsumedEvents).ShouldBe(["AccountOpened", "FundsDeposited"]);
    }

    // ---------------------------------------------------------------- the binding descriptor

    [Fact]
    public void the_specification_binding_carries_the_act_and_the_same_resolved_types()
    {
        var outcome = GeneratorHarness.Run(Usings + Domain + Vocabulary +
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact]
                public async Task a_deposit_after_opening()
                {
                    await GivenNoEventsFor<Account>(Guid.Empty);
                    await WhenReceived(new OpenAccount(Guid.Empty, "Ann"));
                    await WhenReceived(new Deposit(Guid.Empty, 10m));
                    ThenEvents(typeof(FundsDeposited));
                }
            }
            """);
        outcome.CompilationErrors.ShouldBeEmpty();

        var binding = SpecificationBindings.In(load(outcome)).ShouldHaveSingleItem();
        binding.Identity.ShouldBe("Accounts/a deposit after opening");
        binding.CommandType!.Name.ShouldBe("Deposit");
        names(binding.ResolvedTypes).ShouldBe(["Account", "OpenAccount", "Deposit", "FundsDeposited"]);

        // And the slice's own specification agrees with it, type for type
        var slice = GeneratedEventModel.For(load(outcome))!.Slices.ShouldHaveSingleItem();
        names(slice.Specifications.Single().ResolvedTypes).ShouldBe(names(binding.ResolvedTypes));
    }

    [Fact]
    public void a_read_acts_specification_binding_names_no_command_even_after_an_arranging_one()
    {
        var outcome = GeneratorHarness.Run(Usings + Domain + Vocabulary +
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceName = "GET /accounts/{id}")]
                public async Task opened_then_read()
                {
                    await WhenReceived(new OpenAccount(Guid.Empty, "Ann"));
                    await WhenReading<Account>("/accounts/{id}");
                }
            }
            """);
        outcome.CompilationErrors.ShouldBeEmpty();

        // bobcat#501: the name-based fallback would have taken OpenAccount, the arrangement, as the command
        var binding = SpecificationBindings.In(load(outcome)).ShouldHaveSingleItem();
        binding.CommandType.ShouldBeNull();
        binding.SliceName.ShouldBe("GET /accounts/{id}");
    }

    // ---------------------------------------------------------------- across a package boundary

    [Fact]
    public void the_roles_are_read_from_a_referenced_assemblys_metadata()
    {
        // The vocabulary compiled into an assembly of its own: no source for the generator to read, only
        // the attributes in metadata — WolverineFx.Bobcat's position relative to a consumer's suite
        var vocabulary = GeneratorHarness.CompileLibrary("Vocabulary", Usings + Domain + Vocabulary);

        var outcome = GeneratorHarness.Run(
            Usings +
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit_is_credited()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));
                    await WhenPosted(Specify<Deposit>().With(x => x.Amount, 10m), "/deposits");
                    ThenEvents(new FundsDeposited(id, 10m));
                    ThenMessageSent<DepositReceipt>();
                }
            }
            """,
            options: null,
            extraReferences: [MetadataReference.CreateFromImage(vocabulary)]);

        outcome.CompilationErrors.ShouldBeEmpty();

        var slice = GeneratedEventModel.For(load(outcome, vocabulary))!.Slices.Single(x => x.Name == "Deposit");
        slice.CommandType!.FullName.ShouldBe("Bank.Deposit");
        names(slice.AggregateTypes).ShouldBe(["Account"]);
        names(slice.EmittedEvents).ShouldBe(["FundsDeposited"]);
        names(slice.PublishedMessages).ShouldBe(["DepositReceipt"]);
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin!.HttpRoute.ShouldBe("/deposits");
    }

    // ---------------------------------------------------------------- BOBCAT034

    [Fact]
    public void a_role_naming_no_parameter_is_an_error_where_the_vocabulary_is_declared()
    {
        var outcome = GeneratorHarness.Run(
            """
            using System;
            using System.Threading.Tasks;
            using Bobcat;
            using JasperFx.Events.EventModeling;

            public class Scenario
            {
                [EventModelRole(SliceRole.Command, "mesage")]
                public Task WhenReceived(object message) => Task.CompletedTask;

                [EventModelRole(SliceRole.Command, nameof(command))]
                [EventModelTrigger(TriggerKind.Http, Route = "url")]
                public Task WhenPosted(object command, string route) => Task.CompletedTask;

                [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
                public Task Fine<TAggregate>() => Task.CompletedTask;
            }
            """);

        var errors = outcome.WithId("BOBCAT034").Select(x => x.GetMessage()).ToList();
        errors.Count.ShouldBe(2);
        errors.ShouldContain(x => x.Contains("From = \"mesage\"") && x.Contains("It has: message"));
        errors.ShouldContain(x => x.Contains("Route = \"url\"") && x.Contains("command, route"));
    }

    [Fact]
    public void every_slice_role_maps_to_a_role_word()
    {
        // The generator reads SliceRole by member NAME; a member added without a word would stamp nothing
        foreach (var member in Enum.GetNames<SliceRole>())
            ProjectedRoles.RoleWords.ShouldContainKey(member);

        ProjectedRoles.RoleWords.Count.ShouldBe(Enum.GetNames<SliceRole>().Length);
    }

    // ---------------------------------------------------------------- review findings on #473

    [Fact]
    public void a_type_generated_code_cannot_name_stamps_nothing_and_the_suite_still_compiles()
    {
        // typeof(accounts.Withdraw) in BobcatEventModelSource.g.cs or BobcatSpecificationBindings.g.cs is
        // CS0122: the test can name a private, protected or file-local type, generated code cannot
        var outcome = GeneratorHarness.Run(Usings + Domain + Vocabulary +
            """
            namespace Bank
            {
                public record Envelope<T>(T Value);
            }

            file record Secret(Guid Id);

            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                private record Withdraw(Guid Id);
                private record Withdrawn(Guid Id);
                protected record Audited(Guid Id);

                [Fact, BobcatSlice(SliceName = "Withdraw")]
                public async Task a_withdrawal()
                {
                    await GivenEvents<Account>(Guid.Empty, new Secret(Guid.Empty));
                    await WhenReceived(new Withdraw(Guid.Empty));
                    ThenEvents(typeof(Withdrawn), typeof(Audited), typeof(Envelope<Withdrawn>), typeof(Envelope<FundsDeposited>));
                    ThenMessageSent<Envelope<Withdrawn[]>>();
                }
            }
            """);

        outcome.CompilationErrors.ShouldBeEmpty();

        var slice = GeneratedEventModel.For(load(outcome))!.Slices.ShouldHaveSingleItem();
        slice.CommandType.ShouldBeNull();
        names(slice.AggregateTypes).ShouldBe(["Account"]);
        slice.EmittedEvents.ShouldHaveSingleItem().FullName.ShouldStartWith("Bank.Envelope`1[[Bank.FundsDeposited");
        slice.PublishedMessages.ShouldBeEmpty();

        // The name-based fallback for the binding's command is held to the same rule: no later When is
        // promoted, and no typeof(accounts.Withdraw) is written
        var binding = SpecificationBindings.In(load(outcome)).ShouldHaveSingleItem();
        binding.CommandType.ShouldBeNull();
        binding.SliceName.ShouldBe("Withdraw");
        binding.ResolvedTypes.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void a_known_route_beats_an_unknown_one_whichever_specification_folds_first(bool knownFirst)
    {
        var computed =
            """
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task to_a_computed_route() => await WhenPut(new Deposit(Guid.Empty, 1m), $"/accounts/{Guid.NewGuid()}");

                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task to_no_route() => await Post(new Deposit(Guid.Empty, 1m));

            """;
        var known =
            """
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task to_a_constant_route() => await WhenPosted(new Deposit(Guid.Empty, 1m), "/deposits");

            """;

        var slice = describe(
            """
            public class Routes : ScenarioBase
            {
                [EventModelRole(SliceRole.Command, nameof(command))]
                [EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "PUT")]
                public Task WhenPut(object command, string route) => Task.CompletedTask;

                [EventModelRole(SliceRole.Command, nameof(command))]
                [EventModelTrigger(TriggerKind.Http)]
                public Task Post(object command) => Task.CompletedTask;
            }

            [BobcatFeature("Accounts")]
            public class accounts : Routes
            {
            """ + (knownFirst ? known + computed : computed + known) + "}").Single(x => x.Name == "Deposit");

        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin!.HttpRoute.ShouldBe("/deposits");

        // The verb travels with the route it was declared beside, not with an earlier routeless act
        slice.TriggerOrigin.HttpMethod.ShouldBe("POST");
    }

    [Fact]
    public void a_call_in_a_local_function_or_a_lambda_contributes_roles_but_is_never_the_act()
    {
        var slices = describe(
            """
            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task after_the_act()
                {
                    await WhenReceived(new Deposit(Guid.Empty, 1m));
                    Task neverCalled() => WhenReceived(new OpenAccount(Guid.Empty, "x"));
                    Func<Task> later = () => WhenPosted(new OpenAccount(Guid.Empty, "y"), "/accounts");
                    Func<Task> anonymous = delegate { return WhenReceived(new OpenAccount(Guid.Empty, "z")); };
                }

                [Fact, BobcatSlice(SliceType = typeof(OpenAccount))]
                public async Task only_in_a_lambda()
                {
                    await Task.Run(() => WhenReceived(new OpenAccount(Guid.Empty, "w")));
                    ThenEvents(typeof(AccountOpened));
                }
            }
            """);

        var deposit = slices.Single(x => x.Name == "Deposit");
        deposit.CommandType!.Name.ShouldBe("Deposit");
        deposit.TriggerKind.ShouldBeNull();

        // The roles still count: they are types the test exercises, whatever order they run in
        names(deposit.Specifications.Single().ResolvedTypes).ShouldBe(["Deposit", "OpenAccount"]);

        var open = slices.Single(x => x.Name == "OpenAccount");
        names(open.EmittedEvents).ShouldBe(["AccountOpened"]);
        names(open.Specifications.Single().ResolvedTypes).ShouldBe(["OpenAccount", "AccountOpened"]);
    }

    [Fact]
    public void a_role_from_the_declaring_classes_type_parameter_resolves_through_a_closed_scenario_base()
    {
        var outcome = GeneratorHarness.Run(Usings + Domain + Vocabulary +
            """
            namespace Vocab
            {
                public abstract class AggregateScenario<TAggregate> : ScenarioBase where TAggregate : class
                {
                    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
                    public Task GivenStream(Guid id) => Task.CompletedTask;

                    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
                    public virtual Task GivenEmptyStream(Guid id) => Task.CompletedTask;

                    public class Nested
                    {
                        [EventModelRole(SliceRole.ReadModel, nameof(TAggregate))]
                        public Task Check() => Task.CompletedTask;
                    }
                }

                public class AccountScenario : AggregateScenario<Account> { }

                // An override with no attribute of its own: the base declaration's role, read as the
                // constructed base the override sits on
                public class SummaryScenario : AggregateScenario<AccountSummary>
                {
                    public override Task GivenEmptyStream(Guid id) => Task.CompletedTask;
                }
            }

            [BobcatFeature("Accounts")]
            public class accounts : AccountScenario
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit()
                {
                    await GivenStream(Guid.Empty);
                    await new AggregateScenario<Statement>.Nested().Check();
                    await WhenReceived(new Deposit(Guid.Empty, 1m));
                }
            }

            [BobcatFeature("Summaries")]
            public class summaries : SummaryScenario
            {
                [Fact, BobcatSlice(SliceType = typeof(OpenAccount))]
                public async Task an_opening()
                {
                    await GivenEmptyStream(Guid.Empty);
                    await WhenReceived(new OpenAccount(Guid.Empty, "Ann"));
                }
            }
            """);

        outcome.WithId("BOBCAT034").ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();

        var slices = GeneratedEventModel.For(load(outcome))!.Slices;
        var deposit = slices.Single(x => x.Name == "Deposit");
        names(deposit.AggregateTypes).ShouldBe(["Account"]);
        names(deposit.ReadModelTypes).ShouldBe(["Statement"]);

        names(slices.Single(x => x.Name == "OpenAccount").AggregateTypes).ShouldBe(["AccountSummary"]);
    }

    [Fact]
    public void a_forwarding_wrapper_stamps_roles_only_when_it_redeclares_them()
    {
        var slice = describe(
            """
            public class Forwarding : ScenarioBase
            {
                // Hides the vocabulary's: a different method, read for its own attributes only
                public new Task WhenReceived(object message) => base.WhenReceived(message);

                [EventModelRole(SliceRole.Command, nameof(command))]
                public Task Send(object command) => base.WhenReceived(command);
            }

            [BobcatFeature("Accounts")]
            public class accounts : Forwarding
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit))]
                public async Task a_deposit()
                {
                    await WhenReceived(new OpenAccount(Guid.Empty, "Ann"));
                    await Send(new Deposit(Guid.Empty, 1m));
                }
            }
            """).Single(x => x.Name == "Deposit");

        slice.CommandType!.Name.ShouldBe("Deposit");
        names(slice.Specifications.Single().ResolvedTypes).ShouldBe(["Deposit"]);
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyList<EventModelSliceDescriptor> describe(string specs)
    {
        var outcome = GeneratorHarness.Run(Usings + Domain + Vocabulary + specs);
        outcome.CompilationErrors.ShouldBeEmpty();
        return GeneratedEventModel.For(load(outcome))!.Slices;
    }

    internal static string[] names(IEnumerable<JasperFx.Descriptors.TypeDescriptor> types)
        => types.Select(x => x.Name).ToArray();

    internal static Assembly load(GeneratorHarness.RunOutcome outcome, params byte[][] dependencies)
    {
        using var stream = new MemoryStream();
        var emitted = outcome.Compilation.Emit(stream);
        emitted.Success.ShouldBeTrue(string.Join("\n", emitted.Diagnostics));

        var context = new SpecLoadContext();
        foreach (var dependency in dependencies) context.LoadFromStream(new MemoryStream(dependency));
        return context.LoadFromStream(new MemoryStream(stream.ToArray()));
    }

    /// <summary>One context per load, so an in-memory dependency resolves by name and nothing collides.</summary>
    private sealed class SpecLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
            => Assemblies.FirstOrDefault(x => x.GetName().Name == name.Name);
    }
}
