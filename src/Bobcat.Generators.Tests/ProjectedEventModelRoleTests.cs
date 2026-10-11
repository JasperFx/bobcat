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

                [EventModelRole(SliceRole.ReadModel, nameof(T))]
                public Task GivenReadModel<T>(object document) where T : class => Task.CompletedTask;

                [EventModelRole(SliceRole.Command, nameof(message))]
                public Task WhenReceived(object message) => Task.CompletedTask;

                [EventModelRole(SliceRole.Command, nameof(command))]
                [EventModelTrigger(TriggerKind.Http, Route = nameof(route))]
                public Task WhenPosted(object command, string route) => Task.CompletedTask;

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
    public void a_command_slice_states_every_role_the_gherkin_lane_would()
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
                    await WhenPosted(new OpenAccount(id, "Ann"), $"/accounts/{id}");
                }
            }
            """);

        var deposit = slices.Single(x => x.Name == "Deposit");
        deposit.TriggerKind.ShouldBe(TriggerKind.Http);
        deposit.TriggerOrigin!.HttpRoute.ShouldBe("/accounts/deposit");
        deposit.TriggerOrigin.HttpMethod.ShouldBe("POST");

        // The kind is certain; a route the generator cannot know is left off rather than guessed
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

    [Fact]
    public void an_unbound_test_with_roles_falls_back_to_the_feature_title_as_gherkin_does()
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

    // ---------------------------------------------------------------- the two arms agree

    private const string GherkinFixture =
        """
        [FixtureTitle("Accounts")]
        public class AccountsFixture : Fixture
        {
            [Given("no events for {aggregate} {string}")]
            public void GivenNoEvents(Type aggregate, string id) { }

            [Given("{event} occurred")]
            public void GivenEventOccurred(Type @event) { }

            [When("{command} is received")]
            public void WhenReceived(Type command) { }

            [Then("{event} is emitted")]
            public void ThenEmitted(Type @event) { }

            [Then("the {readmodel} read model exists")]
            public void ThenReadModel(Type readModel) { }

            [Then("{message} is sent")]
            public void ThenSent(Type message) { }
        }
        """;

    private const string GherkinFeature =
        """
        Feature: Accounts

          @slice:Deposit @domain:Banking
          Scenario: A deposit after opening
            Given no events for Account "11111111-1111-1111-1111-111111111111"
            And AccountOpened occurred
            When OpenAccount is received
            And Deposit is received
            Then FundsDeposited is emitted
            And DepositReceipt is sent

          @slice:AccountSummary @domain:Banking
          Scenario: The summary shows the balance
            Given no events for Account "11111111-1111-1111-1111-111111111111"
            And AccountOpened occurred
            And FundsDeposited occurred
            Then the AccountSummary read model exists
        """;

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
    public void the_projected_arm_produces_what_the_gherkin_arm_does_for_the_same_scenarios()
    {
        var gherkinOutcome = GeneratorHarness.Run(Usings + Domain + Vocabulary + GherkinFixture,
            ("Accounts.feature", GherkinFeature));
        gherkinOutcome.CompilationErrors.ShouldBeEmpty();
        var gherkin = GeneratedEventModel.For(load(gherkinOutcome))!.Slices;

        var projected = describe(ProjectedEquivalent);

        gherkin.Select(x => x.Name).OrderBy(x => x).ShouldBe(["AccountSummary", "Deposit"]);
        projected.Select(x => x.Name).OrderBy(x => x).ShouldBe(["AccountSummary", "Deposit"]);

        foreach (var name in new[] { "Deposit", "AccountSummary" })
        {
            var g = gherkin.Single(x => x.Name == name);
            var p = projected.Single(x => x.Name == name);

            p.CommandType?.FullName.ShouldBe(g.CommandType?.FullName, $"{name}: command");
            p.Pattern.ShouldBe(g.Pattern, $"{name}: pattern");
            p.Domain.ShouldBe(g.Domain, $"{name}: domain");
            names(p.AggregateTypes).ShouldBe(names(g.AggregateTypes), $"{name}: aggregates");
            names(p.EmittedEvents).ShouldBe(names(g.EmittedEvents), $"{name}: emitted events");
            names(p.ConsumedEvents).ShouldBe(names(g.ConsumedEvents), $"{name}: consumed events");
            names(p.ReadModelTypes).ShouldBe(names(g.ReadModelTypes), $"{name}: read models");
            names(p.PublishedMessages).ShouldBe(names(g.PublishedMessages), $"{name}: messages");
            names(p.Specifications.Single().ResolvedTypes)
                .ShouldBe(names(g.Specifications.Single().ResolvedTypes), $"{name}: resolved types");
        }

        // And what they agree ON is the right reading, not two arms agreeing on nothing
        var deposit = projected.Single(x => x.Name == "Deposit");
        deposit.CommandType!.Name.ShouldBe("Deposit");
        deposit.ConsumedEvents.ShouldBeEmpty();
        names(projected.Single(x => x.Name == "AccountSummary").ConsumedEvents).ShouldBe(["AccountOpened", "FundsDeposited"]);
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
