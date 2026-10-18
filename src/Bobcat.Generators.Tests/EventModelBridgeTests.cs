using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shouldly;
using static Bobcat.Generators.Tests.ProjectedEventModelRoleTests;

namespace Bobcat.Generators.Tests;

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
                [EventModelTrigger(TRIGGER.Http, Route = nameof(route))]
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

    /// <summary>
    /// The JSON the publisher put on the wire before bobcat#744, captured from <c>GeneratedEventModel.For</c>
    /// plus the publisher's own serializer options, and committed as the pin the bridge is held to.
    /// </summary>
    [Fact]
    public void the_publisher_json_matches_the_pin()
    {
        var outcome = GeneratorHarness.Run(Fixture.Replace("TRIGGER", "TriggerKind"));
        outcome.CompilationErrors.ShouldBeEmpty();

        var wire = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(namingPolicy: null) }
        };

        var json = JsonSerializer.Serialize(GeneratedEventModel.For(load(outcome)), wire);
        json.ShouldBe(File.ReadAllText(fixtureFile()));
    }
}
