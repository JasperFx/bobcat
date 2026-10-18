using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shouldly;
using static Bobcat.Generators.Tests.ProjectedEventModelRoleTests;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#744's publishing bridge: core no longer references JasperFx, so the generated code serializes the spec
/// assembly's Event Model half itself and registers it with <c>Bobcat.Monitoring.SpecEventModels</c>. The JSON is
/// pinned against what core's publisher sent before the change.
/// </summary>
public class EventModelBridgeTests
{
    internal const string Fixture =
        """
        using System;
        using System.Threading.Tasks;
        using Bobcat;
        using JasperFx.Events.EventModeling;
        using Xunit;

        [assembly: Bobcat.EventModelName("BankAccountES")]

        namespace Bank
        {
            public record OpenAccount(Guid Id, string Owner);
            public record Deposit(Guid Id, decimal Amount);
            public record AccountOpened(Guid Id, string Owner);
            public record FundsDeposited(Guid Id, decimal Amount);
            public record DepositReceipt(Guid Id);
            public class Account { public Guid Id { get; set; } }
            public class AccountSummary { public Guid Id { get; set; } }
            public record ClientEnrolled(Guid Id, string Name);
            public class Client { public Guid Id { get; set; } }
        }

        namespace Vocab
        {
            public abstract class ScenarioBase
            {
                [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
                [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
                public Task GivenEvents<TAggregate>(Guid id, params object[] events) where TAggregate : class => Task.CompletedTask;

                [EventModelRole(SliceRole.Context, nameof(TContext))]
                [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
                public Task GivenContext<TContext>(Guid id, params object[] events) where TContext : class => Task.CompletedTask;

                [EventModelRole(SliceRole.Command, nameof(command))]
                [EventModelTrigger(ActTrigger.Http, Route = nameof(route))]
                public Task WhenPosted(object command, string route) => Task.CompletedTask;

                [EventModelRole(SliceRole.Event, nameof(events))]
                public void ThenEvents(params Type[] events) { }

                [EventModelRole(SliceRole.ReadModel, nameof(T))]
                public Task<T> ThenReadModel<T>(object id) where T : class => Task.FromResult<T>(null!);

                [EventModelRole(SliceRole.Message, nameof(T))]
                public void ThenMessageSent<T>() { }
            }
        }

        namespace Specs
        {
            using Bank;
            using Vocab;

            [BobcatFeature("Accounts")]
            public class accounts : ScenarioBase
            {
                [Fact, BobcatSlice(SliceType = typeof(Deposit), Domain = "Banking", Chapter = "Money")]
                public async Task a_deposit_is_credited()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));
                    await WhenPosted(new Deposit(id, 10m), "/accounts/deposit");
                    ThenEvents(typeof(FundsDeposited));
                    ThenMessageSent<DepositReceipt>();
                }

                [Fact, BobcatSlice(SliceType = typeof(OpenAccount), TriggeredBy = "Teller")]
                public async Task an_account_is_opened_for_an_enrolled_client()
                {
                    var client = Guid.NewGuid();
                    await GivenContext<Client>(client, new ClientEnrolled(client, "Ann"));
                    await WhenPosted(new OpenAccount(Guid.NewGuid(), "Ann"), "/accounts");
                    ThenEvents(typeof(AccountOpened));
                }
            }

            [BobcatFeature("Summaries")]
            [BobcatSlice(SliceName = "AccountSummary")]
            public class account_summary : ScenarioBase
            {
                [Fact]
                public async Task the_summary_shows_the_balance()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new AccountOpened(id, "Ann"), new FundsDeposited(id, 10m));
                    await ThenReadModel<AccountSummary>(id);
                }
            }
        }
        """;

    private static string fixtureFile([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "bridge-event-model.json");

    private static System.Reflection.Assembly compiled()
    {
        var outcome = GeneratorHarness.Run(Fixture);
        outcome.CompilationErrors.ShouldBeEmpty();
        return load(outcome);
    }

    /// <summary>
    /// The pin itself: what core's publisher put on the wire before bobcat#744 — <c>GeneratedEventModel.For</c>
    /// (describe with an empty service provider, stamp the source's provenance) serialized with the publisher's
    /// options — captured into <c>Fixtures/bridge-event-model.json</c> from the pre-#744 code and still true of
    /// that path.
    /// </summary>
    [Fact]
    public void the_publisher_json_matches_the_pin()
    {
        var wire = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(namingPolicy: null) }
        };

        var json = JsonSerializer.Serialize(GeneratedEventModel.For(compiled()), wire);
        json.ShouldBe(File.ReadAllText(fixtureFile()));
    }

    /// <summary>
    /// bobcat#744: the generated bridge registers the half with core and its JSON is byte-identical to what the
    /// publisher used to send, so a console sees no difference.
    /// </summary>
    [Fact]
    public void the_bridge_registers_json_byte_identical_to_the_pin()
    {
        var assembly = compiled();
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);

        var registration = Bobcat.Monitoring.SpecEventModels.All.Last(x => x.Source == "SpecProject");
        registration.ModelName.ShouldBe("BankAccountES");
        registration.Json().ShouldBe(File.ReadAllText(fixtureFile()));
    }

    [Fact]
    public void the_bridge_is_emitted_beside_the_source_and_names_the_model_and_the_assembly()
    {
        var outcome = GeneratorHarness.Run(Fixture);
        outcome.CompilationErrors.ShouldBeEmpty();

        var source = outcome.GeneratedSource("BobcatEventModelSource");
        source.ShouldContain("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        source.ShouldContain("global::Bobcat.Monitoring.SpecEventModels.Register(\"BankAccountES\", \"SpecProject\", static () => Json());");
    }

    [Fact]
    public void a_suite_that_models_nothing_registers_no_half()
    {
        var outcome = GeneratorHarness.Run(
            """
            using Bobcat;
            using Xunit;

            [BobcatFeature("Plain")]
            public class plain
            {
                [Fact]
                public void nothing_modelled()
                {
                    // Given nothing
                }
            }
            """);
        outcome.CompilationErrors.ShouldBeEmpty();

        outcome.Result.Results.SelectMany(x => x.GeneratedSources)
            .ShouldNotContain(x => x.HintName.Contains("BobcatEventModelSource"));
    }
}
