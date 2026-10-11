using Bobcat.Engine;
using Bobcat.Runtime;
using Fisher;
using JasperFx;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// wolverine#4938: the explicit stream-and-version vocabulary on <see cref="CritterStackFixture"/>, for an
/// application that does not use Wolverine. Every host here registers an event store and nothing else —
/// no Wolverine, no message bus — and the fixture's own act (<c>DispatchAsync</c>) refuses to run, so a
/// pass proves none of it needed one. Each case runs on Marten (Postgres 5445) and on Fisher (a temp
/// SQLite file), because the vocabulary binds to JasperFx.Events only.
/// </summary>
public class StreamVersionVocabularyTests
{
    private enum Store
    {
        Marten,
        Fisher
    }

    [PostgresFact]
    public Task the_stream_gains_only_what_follows_the_named_version_on_marten()
        => theStreamGainsOnlyWhatFollowsTheNamedVersion(Store.Marten);

    [Fact]
    public Task the_stream_gains_only_what_follows_the_named_version_on_fisher()
        => theStreamGainsOnlyWhatFollowsTheNamedVersion(Store.Fisher);

    [PostgresFact]
    public Task acting_on_a_stream_reads_as_one_paragraph_on_marten() => actingOnAStreamReadsAsOneParagraph(Store.Marten);

    [Fact]
    public Task acting_on_a_stream_reads_as_one_paragraph_on_fisher() => actingOnAStreamReadsAsOneParagraph(Store.Fisher);

    [PostgresFact]
    public Task a_wrong_event_fails_only_the_then_sentence_on_marten() => aWrongEventFailsOnlyTheThenSentence(Store.Marten);

    [Fact]
    public Task a_wrong_event_fails_only_the_then_sentence_on_fisher() => aWrongEventFailsOnlyTheThenSentence(Store.Fisher);

    [PostgresFact]
    public Task a_failing_act_is_captured_and_reported_by_the_check_on_marten()
        => aFailingActIsCapturedAndReportedByTheCheck(Store.Marten);

    [Fact]
    public Task a_failing_act_is_captured_and_reported_by_the_check_on_fisher()
        => aFailingActIsCapturedAndReportedByTheCheck(Store.Fisher);

    private static async Task theStreamGainsOnlyWhatFollowsTheNamedVersion(Store store)
    {
        await using var scenario = await StoreOnlyScenario.Start(store);
        var fixture = scenario.Fixture;
        var id = Guid.NewGuid();

        await fixture.GivenEvents<Wallet>(id, new WalletOpened(id, "Ann"), new WalletCredited(id, 10));
        (await fixture.TheStreamVersion(id)).ShouldBe(2);

        // Whatever appends next is not this fixture's act, and does not have to be
        await EventStoreAuthoring.AppendAsync(scenario.EventStore, typeof(Wallet), id, [new WalletCredited(id, 25)]);

        await fixture.ThenEventsOn<Wallet>(id, 2, new WalletCredited(id, 25));
        await fixture.ThenEventsOn<Wallet>(id, 2, Specifications.Specify<WalletCredited>().With(x => x.Amount, 25m));
        await fixture.ThenNoEventsOn<Wallet>(id, 3);

        // Exactly these: a wrong value, a missing event, and an extra one are all wrongs
        await Should.ThrowAsync<SpecAssertionException>(() => fixture.ThenEventsOn<Wallet>(id, 2, new WalletCredited(id, 99)));
        await Should.ThrowAsync<SpecAssertionException>(() => fixture.ThenNoEventsOn<Wallet>(id, 2));
        var extra = await Should.ThrowAsync<SpecAssertionException>(() =>
            fixture.ThenEventsOn<Wallet>(id, 1, new WalletCredited(id, 25)));
        extra.Message.ShouldContain("after version 1");

        // A version past the end of the stream would make every check vacuous
        var ahead = await Should.ThrowAsync<SpecCriticalException>(() => fixture.ThenNoEventsOn<Wallet>(id, 5));
        ahead.Message.ShouldContain("is at version 3");
    }

    private static async Task actingOnAStreamReadsAsOneParagraph(Store store)
    {
        await using var scenario = await StoreOnlyScenario.Start(store);
        var fixture = scenario.Fixture;
        var id = Guid.NewGuid();

        using var recording = ScenarioRecorder.Begin("Wallets", "crediting by hand", null, Guid.NewGuid());

        await fixture.GivenEvents<Wallet>(id, new WalletOpened(id, "Bea"));
        await fixture.WhenActingOn<Wallet>(id, "the wallet is credited by hand",
            services => EventStoreAuthoring.AppendAsync(services.EventStore(), typeof(Wallet), id, [new WalletCredited(id, 40)]),
            new WalletCredited(id, 40));

        var name = ScenarioValues.Format(id);
        recording.Steps.Select(x => $"{x.Keyword} {x.Text}").ShouldBe([
            $"Given the Wallet stream {name} is at version 1",
            "When the wallet is credited by hand",
            "Then WalletCredited is emitted on it"
        ]);
        recording.Steps.ShouldAllBe(x => x.Status == ResultStatus.success);

        // The capture feeds the rest of the vocabulary like any other act's
        fixture.LastExecution.Error.ShouldBeNull();
        fixture.LastExecution.NewEvents.Select(x => x.Data).ShouldBe([new WalletCredited(id, 40)]);
        fixture.ThenEvents(new WalletCredited(id, 40));

        // The undescribed form names the stream instead, and an act that appends nothing passes a check for nothing
        await fixture.WhenActingOn<Wallet>(id, _ => Task.CompletedTask);
        recording.Steps.Skip(3).Select(x => $"{x.Keyword} {x.Text}").ShouldBe([
            $"Given the Wallet stream {name} is at version 2",
            $"When the Wallet stream {name} is acted on",
            "Then no events are emitted on it"
        ]);
    }

    private static async Task aWrongEventFailsOnlyTheThenSentence(Store store)
    {
        await using var scenario = await StoreOnlyScenario.Start(store);
        var fixture = scenario.Fixture;
        var id = Guid.NewGuid();

        using var recording = ScenarioRecorder.Begin("Wallets", "the wrong event", null, Guid.NewGuid());

        await fixture.GivenEvents<Wallet>(id, new WalletOpened(id, "Cy"));
        var wrong = await Should.ThrowAsync<SpecAssertionException>(() => fixture.WhenActingOn<Wallet>(id,
            services => EventStoreAuthoring.AppendAsync(services.EventStore(), typeof(Wallet), id, [new WalletCredited(id, 5)]),
            new WalletDebited(id, 5)));

        wrong.Message.ShouldContain("after version 1");
        recording.Steps.Select(x => x.Status).ShouldBe([ResultStatus.success, ResultStatus.success, ResultStatus.failed]);
    }

    private static async Task aFailingActIsCapturedAndReportedByTheCheck(Store store)
    {
        await using var scenario = await StoreOnlyScenario.Start(store);
        var fixture = scenario.Fixture;
        var id = Guid.NewGuid();

        await fixture.GivenEvents<Wallet>(id, new WalletOpened(id, "Di"));
        var failed = await Should.ThrowAsync<SpecAssertionException>(() => fixture.WhenActingOn<Wallet>(id,
            _ => throw new InvalidOperationException("The wallet is frozen"),
            new WalletCredited(id, 5)));

        failed.Message.ShouldContain("the act failed");
        failed.Message.ShouldContain("The wallet is frozen");

        // Captured, as WhenCommand captures: the throwing-refusal step reads it
        fixture.LastExecution.Error.ShouldBeOfType<InvalidOperationException>();
        fixture.ThenValidationFails("frozen");
        await fixture.ThenNoEventsOn<Wallet>(id, 1).ShouldThrowAsync<SpecAssertionException>();
    }

    /// <summary>A Critter Stack fixture with no message bus behind it: its bus act refuses to run.</summary>
    private sealed class StoreOnlyWalletFixture : CritterStackFixture
    {
        protected override Task<IActOutcome> DispatchAsync(object command, int timeoutInMilliseconds)
            => throw new NotSupportedException("This application has no message bus; act through WhenActingOn.");
    }

    /// <summary>One scenario's worth of host, scope and fixture, on a store-only host.</summary>
    private sealed class StoreOnlyScenario : IAsyncDisposable
    {
        private readonly HostResource _resource;
        private readonly string? _fisherFile;

        private StoreOnlyScenario(HostResource resource, string? fisherFile)
        {
            _resource = resource;
            _fisherFile = fisherFile;
        }

        public StoreOnlyWalletFixture Fixture { get; } = new();

        public IEventStore EventStore => _resource.RootServices.EventStore();

        public static async Task<StoreOnlyScenario> Start(Store store)
        {
            var fisherFile = store == Store.Fisher
                ? Path.Combine(Path.GetTempPath(), $"bobcat-stream-version-{Guid.NewGuid():N}.db")
                : null;

            var resource = new HostResource(() =>
            {
                var builder = Host.CreateApplicationBuilder();
                if (fisherFile is null)
                {
                    builder.Services.AddMarten(opts =>
                    {
                        opts.Connection(PostgresEnvironment.ConnectionString);
                        opts.DatabaseSchemaName = "bobcat_stream_version";
                        opts.AutoCreateSchemaObjects = AutoCreate.All;
                    });
                }
                else
                {
                    // Fisher builds its schema lazily; the first append wants it applied at startup
                    builder.Services.AddFisher(opts => opts.Connection($"Data Source={fisherFile}"))
                        .ApplyAllDatabaseChangesOnStartup();
                }

                return builder.Build();
            });

            await resource.StartAsync(TestContext.Current.CancellationToken);

            var resources = new TestResources();
            resources.Add(resource);
            await resource.BeginScenarioScope();

            var scenario = new StoreOnlyScenario(resource, fisherFile);
            scenario.Fixture.Context = new SpecExecutionContext("stream version", resources: resources)
            {
                Cancellation = TestContext.Current.CancellationToken
            };
            scenario.Fixture.BeforeEach();
            return scenario;
        }

        public async ValueTask DisposeAsync()
        {
            await _resource.EndScenarioScope();
            await _resource.DisposeAsync();

            if (_fisherFile is null) return;
            foreach (var file in new[] { _fisherFile, _fisherFile + "-wal", _fisherFile + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // A temp file; the OS reclaims it
                }
            }
        }
    }
}
