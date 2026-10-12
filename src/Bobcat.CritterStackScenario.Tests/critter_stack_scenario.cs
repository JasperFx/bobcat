using Bobcat.CritterStack;
using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Engine.Verification;
using JasperFx.Events.Tags;
using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

// bobcat#471, phase 1: every member of the store-only vocabulary, on Marten and on Fisher, with no message
// bus registered anywhere. Each test runs once per store through the two derived classes at the bottom.

public abstract class critter_stack_scenario<THost>(THost store) where THost : StoreHost
{
    private CritterStackScenario newScenario()
    {
        Assert.SkipUnless(store.IsAvailable, store.SkipReason);
        return new CritterStackScenario(store.Host);
    }

    // A store-only act: whatever an application does, here an append through the store's own abstractions
    private static Func<IServiceProvider, Task> appending(Type aggregate, Guid id, params object[] events)
        => services => EventStoreAuthoring.AppendAsync(services.EventStore(), aggregate, id, events);

    private static Func<IServiceProvider, Task> appending(Guid id, params object[] events)
        => appending(typeof(Wallet), id, events);

    // ---- arrange ---------------------------------------------------------------------------

    [Fact]
    public async Task givens_append_and_read_as_the_wolverine_vocabulary_reads()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Ann"), new WalletCredited(id, 10));
            await scenario.GivenEventsOn<Wallet>(other, new WalletOpened(other, "Bea"));
            await scenario.GivenNoEventsFor<Wallet>(Guid.NewGuid());
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Select(x => x.Keyword).ShouldBe(["Given", "And", "And"]);
        recording.Steps[0].Text.ShouldStartWith("the Wallet stream has already recorded WalletOpened(");
        recording.Steps[1].Text.ShouldContain("has already recorded WalletOpened(");
        recording.Steps[2].Text.ShouldEndWith("has no events yet");

        (await scenario.TheAggregate<Wallet>(id))!.Balance.ShouldBe(10);
        (await scenario.TheAggregate<Wallet>(other))!.Owner.ShouldBe("Bea");
        (await scenario.TheStreamVersion(id)).ShouldBe(2);
    }

    [Fact]
    public async Task a_strong_typed_id_and_a_partial_object_arrange_a_stream()
    {
        var scenario = newScenario();
        var id = new WalletId(Guid.NewGuid());

        await scenario.GivenEvents<Wallet>(id,
            new WalletOpened(id.Value, "Cy"),
            Specifications.Specify<WalletCredited>().With(x => x.WalletId, id.Value).With(x => x.Amount, 7m));

        (await scenario.TheAggregate<Wallet>(id))!.Balance.ShouldBe(7);
        await scenario.ThenAggregate<Wallet>(id, Specifications.Specify<Wallet>().With(x => x.Owner, "Cy"));
    }

    [Fact]
    public async Task an_aggregate_less_stream_is_arranged_by_its_id_alone()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(() => scenario.GivenEvents(id, new WalletOpened(id, "Di")));

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Single().Text.ShouldStartWith("the stream has already recorded WalletOpened(");
        (await scenario.TheStreamVersion(id)).ShouldBe(1);
    }

    [Fact]
    public async Task a_context_stream_is_arranged_exactly_as_given_events_and_never_becomes_the_scenarios_stream()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        var lookup = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Ann"), new WalletCredited(id, 3));
            // bobcat#502: a lookup the act reads, arranged after the stream the act decides against
            await scenario.GivenContext<Wallet>(lookup, new WalletOpened(lookup, "Bea"));
            await scenario.GivenContext(typeof(Wallet), new WalletId(Guid.NewGuid()));
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Select(x => x.Keyword).ShouldBe(["Given", "And", "And"]);
        recording.Steps[1].Text.ShouldContain("has already recorded WalletOpened(");
        recording.Steps[2].Text.ShouldEndWith("has no events yet");

        // The same append GivenEvents makes
        (await scenario.TheAggregate<Wallet>(lookup))!.Owner.ShouldBe("Bea");
        (await scenario.TheStreamVersion(lookup)).ShouldBe(1);

        // ...but the stream the scenario is about is still the one GivenEvents named
        (await scenario.ThenSingleReadModel<Wallet>()).Balance.ShouldBe(3);
    }

    [Fact]
    public async Task a_read_model_is_arranged_directly_and_read_back_as_a_document()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.GivenReadModel<Customer>(Specifications.Specify<Customer>().With(x => x.Id, id).With(x => x.Name, "Ed"));
            await scenario.ThenDocument<Customer>(id, Specifications.Specify<Customer>().With(x => x.Name, "Ed"));
            await scenario.ThenReadModel<Customer>(id);
            await scenario.ThenNoReadModel<Customer>(Guid.NewGuid());
            await scenario.ThenNoReadModel<Ghost>();
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps[0].Text.ShouldStartWith("the Customer read model is Customer(");

        // A document that is not there is a wrong, not a pass
        var missing = await Recordings.RecordAsync(() => scenario.ThenNoReadModel<Customer>(id));
        missing.GatheredFailures().ShouldNotBeNull().ShouldContain("Expected no Customer document");
    }

    // ---- the store-only act ------------------------------------------------------------------

    [Fact]
    public async Task a_store_only_act_is_judged_by_what_the_store_gained()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Flo"));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.WhenActing("the wallet is credited twice",
                appending(id, new WalletCredited(id, 5), new WalletCredited(id, 6)));

            scenario.ThenEvents(typeof(WalletCredited), typeof(WalletCredited));
            scenario.ThenEvents(new WalletCredited(id, 5), new WalletCredited(id, 6));
            scenario.ThenEventsInAnyOrder(new WalletCredited(id, 6), new WalletCredited(id, 5));
            scenario.ThenEventsInAnyOrder(typeof(WalletCredited), typeof(WalletCredited));
            scenario.ThenEmitted(Specifications.Specify<WalletCredited>().With(x => x.Amount, 6m));
            scenario.ThenEmitted<WalletCredited>();
            scenario.ThenNotEmitted<WalletDebited>();
            scenario.ThenNotEmitted(new WalletCredited(id, 99));
            scenario.ThenEventsOn<Wallet>(id, new WalletCredited(id, 5), new WalletCredited(id, 6));
            scenario.ThenNoEventsOn<Wallet>(Guid.NewGuid());
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps[0].Text.ShouldBe("the wallet is credited twice");
        recording.Steps[0].Keyword.ShouldBe("When");
        recording.Steps[1].Text.ShouldBe("WalletCredited, WalletCredited are emitted");

        scenario.LastAct.Error.ShouldBeNull();
        scenario.LastAct.NewEvents.Count.ShouldBe(2);
        scenario.LastAct.AllNewEvents.Count.ShouldBe(2);
        scenario.LastAct.MessagesSent.ShouldBeEmpty();
        // bobcat#504: one store, nothing to tell apart
        scenario.LastAct.StoreByEvent.ShouldBeEmpty();
        scenario.TheEvents.ShouldBe([new WalletCredited(id, 5), new WalletCredited(id, 6)]);
        Should.Throw<SpecAssertionException>(() => scenario.TheEvent<WalletCredited>()).Message.ShouldContain("exactly one");

        // A wrong expectation is gathered on its step, never thrown, inside a recording
        var wrong = await Recordings.RecordAsync(() =>
        {
            scenario.ThenEvents(new WalletCredited(id, 5));
            scenario.ThenEvents(typeof(WalletDebited));
            return Task.CompletedTask;
        });
        wrong.Steps.Select(x => x.Status).ShouldBe([ResultStatus.failed, ResultStatus.failed]);
    }

    // ---- attribution by stream (bobcat#503) --------------------------------------------------

    // Another test's append to a stream it arranged itself, landing while this scenario's act runs
    private static async Task anotherTestAppends(IServiceProvider services, Guid stream, params object[] events)
        => await EventStoreAuthoring.AppendAsync(services.EventStore(), typeof(Wallet), stream, events);

    [Fact]
    public async Task a_concurrent_append_to_a_stream_the_spec_never_arranged_is_foreign()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        var elsewhere = Guid.NewGuid();
        await new CritterStackScenario(store.Host).GivenEvents<Wallet>(elsewhere, new WalletOpened(elsewhere, "Other"));
        await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Ann"));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.WhenActing("the wallet is credited while another test writes", async services =>
            {
                await EventStoreAuthoring.AppendAsync(services.EventStore(), typeof(Wallet), id, [new WalletCredited(id, 5)]);
                await anotherTestAppends(services, elsewhere, new WalletDebited(elsewhere, 1));
            });

            // Whole-store steps judge only the specification's own streams
            scenario.ThenEvents(new WalletCredited(id, 5));
            scenario.ThenNotEmitted<WalletDebited>();

            // A step that names the stream still sees what was appended there
            scenario.ThenEventsOn<Wallet>(elsewhere, new WalletDebited(elsewhere, 1));
        });

        recording.GatheredFailures().ShouldBeNull();
        scenario.LastAct.NewEvents.Select(x => x.Data).ShouldBe([new WalletCredited(id, 5)]);
        scenario.LastAct.AllNewEvents.Select(x => x.Data).ShouldBe([new WalletCredited(id, 5)]);
        scenario.LastAct.ForeignEvents.Select(x => x.Data).ShouldBe([new WalletDebited(elsewhere, 1)]);
        scenario.TheEvents.ShouldBe([new WalletCredited(id, 5)]);
    }

    [Fact]
    public async Task a_failing_whole_store_assertion_suspects_cross_talk_and_a_passing_one_stays_quiet()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        var elsewhere = Guid.NewGuid();
        await new CritterStackScenario(store.Host).GivenEvents<Wallet>(elsewhere, new WalletOpened(elsewhere, "Other"));
        await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Ann"));

        await scenario.WhenActing("the wallet is credited while another test writes", async services =>
        {
            await EventStoreAuthoring.AppendAsync(services.EventStore(), typeof(Wallet), id, [new WalletCredited(id, 5)]);
            await anotherTestAppends(services, elsewhere, new WalletDebited(elsewhere, 1), new WalletDebited(elsewhere, 2));
        });

        var passing = await Recordings.RecordAsync(() =>
        {
            scenario.ThenEvents(typeof(WalletCredited));
            return Task.CompletedTask;
        });
        passing.GatheredFailures().ShouldBeNull();

        var failing = await Recordings.RecordAsync(() =>
        {
            scenario.ThenEvents(typeof(WalletDebited));
            scenario.ThenEvents(new WalletCredited(id, 6));
            scenario.ThenNoEvents();
            return Task.CompletedTask;
        });

        var failures = failing.GatheredFailures().ShouldNotBeNull();
        failing.Steps.Select(x => x.Status).ShouldAllBe(x => x == ResultStatus.failed);
        failures.ShouldContain("cross-talk suspected: 2 event(s) on streams this specification never arranged or started were appended during the act. Is another test writing to this store concurrently?");

        // Outside a recording the wrong is thrown, with the same line
        Should.Throw<SpecAssertionException>(() => scenario.TheEvent<WalletDebited>()).Message.ShouldContain("cross-talk suspected");
    }

    [Fact]
    public async Task a_failing_assertion_with_nothing_foreign_suspects_nothing()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Ann"));
        await scenario.WhenActing("the wallet is credited", appending(id, new WalletCredited(id, 5)));

        var failing = await Recordings.RecordAsync(() =>
        {
            scenario.ThenEvents(typeof(WalletDebited));
            return Task.CompletedTask;
        });

        scenario.LastAct.ForeignEvents.ShouldBeEmpty();
        failing.GatheredFailures().ShouldNotBeNull().ShouldNotContain("cross-talk");
    }

    [Fact]
    public async Task a_stream_an_earlier_act_started_or_an_act_addressed_is_the_specifications_own()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        var addressed = Guid.NewGuid();
        await new CritterStackScenario(store.Host).GivenEvents<Wallet>(addressed, new WalletOpened(addressed, "Bea"));

        // The first act starts the stream; the second appends to it, and nothing arranged it
        await scenario.WhenActing("a wallet is opened", appending(id, new WalletOpened(id, "Ann")));
        await scenario.WhenActing("the wallet is credited", appending(id, new WalletCredited(id, 5)));
        scenario.LastAct.NewEvents.Select(x => x.Data).ShouldBe([new WalletCredited(id, 5)]);
        scenario.LastAct.ForeignEvents.ShouldBeEmpty();

        // A stream arranged by someone else, but addressed by the act, is the specification's too
        await scenario.WhenActingOn<Wallet>(addressed, appending(addressed, new WalletCredited(addressed, 2)),
            new WalletCredited(addressed, 2));
        scenario.LastAct.NewEvents.Select(x => x.Data).ShouldBe([new WalletCredited(addressed, 2)]);
        scenario.LastAct.ForeignEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_act_that_starts_a_stream_names_it()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.WhenActing("a wallet is opened", appending(id, new WalletOpened(id, "Gus")));
            await scenario.ThenStreamIsStarted<Wallet>(id);
            await scenario.ThenStreamIsStartedWithEvents<Wallet>(new WalletOpened(id, "Gus"));
            await scenario.ThenSingleReadModel<Wallet>(Specifications.Specify<Wallet>().With(x => x.Owner, "Gus"));
        });

        recording.GatheredFailures().ShouldBeNull();
        scenario.TheStartedStream<Wallet>().ShouldBe(id);
        scenario.LastAct.StartedStreams.ShouldHaveSingleItem().Id.ShouldBe(id);
        Should.Throw<SpecCriticalException>(() => scenario.TheStartedStreamKey<Wallet>());
    }

    [Fact]
    public async Task an_act_that_appends_nothing_and_throws_nothing_is_a_clean_refusal()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Hal"));

        // No act yet: a refusal cannot be claimed
        var noAct = await Recordings.RecordAsync(() =>
        {
            scenario.ThenCommandRefused();
            return Task.CompletedTask;
        });
        noAct.GatheredFailures().ShouldNotBeNull().ShouldContain("no act has run");

        var refused = await Recordings.RecordAsync(async () =>
        {
            await scenario.WhenActing("the debit is refused", _ => Task.CompletedTask);
            scenario.ThenCommandRefused();
            scenario.ThenNoEvents();
        });
        refused.GatheredFailures().ShouldBeNull();
        refused.Steps[1].Text.ShouldBe("the command is refused");

        var appended = await Recordings.RecordAsync(async () =>
        {
            await scenario.WhenActing("the debit goes through", appending(id, new WalletDebited(id, 1)));
            scenario.ThenCommandRefused();
        });
        appended.GatheredFailures().ShouldNotBeNull().ShouldContain("emitted 1 event(s)");

        var threw = await Recordings.RecordAsync(async () =>
        {
            await scenario.WhenActing("the debit throws", _ => throw new WalletFrozen(id));
            scenario.ThenCommandRefused();
        });
        threw.GatheredFailures().ShouldNotBeNull().ShouldContain("cleanly refused, but it threw");
    }

    // ---- refusals ----------------------------------------------------------------------------

    [Fact]
    public async Task a_refusal_is_found_inside_the_exceptions_the_act_wrapped_it_in()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Ivy"));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.WhenActing("the wallet is credited",
                _ => throw new AggregateException(new InvalidOperationException("The credit failed", new WalletFrozen(id))));

            scenario.ThenValidationFails("is frozen");
            scenario.ThenRefusedWith("is frozen");
            scenario.ThenRefusedWith("is frozen", id);
            scenario.ThenRefusedWith<WalletFrozen>(Specifications.Specify<WalletFrozen>().With(x => x.WalletId, id));
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps[1].Text.ShouldBe("validation fails with \"is frozen\"");
        recording.Steps[1].Cells.Single(x => x.Name == "reason").Actual.ShouldBe($"The wallet {id} is frozen");
        scenario.LastAct.Error.ShouldBeOfType<AggregateException>();

        // A step that asked about events reports the act failing, not a missing event
        var events = await Recordings.RecordAsync(() =>
        {
            scenario.ThenEvents(typeof(WalletCredited));
            return Task.CompletedTask;
        });
        events.GatheredFailures().ShouldNotBeNull().ShouldContain("The act failed: AggregateException");
    }

    [Fact]
    public async Task a_reason_is_matched_exactly_for_now()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        await scenario.WhenActing("the wallet is credited", _ => throw new WalletFrozen(id));

        // bobcat#471 left exact-vs-ignore-case open; exact (Ordinal) until it is decided
        var recording = await Recordings.RecordAsync(() =>
        {
            scenario.ThenValidationFails("IS FROZEN");
            return Task.CompletedTask;
        });

        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("Expected a refusal containing \"IS FROZEN\"");
    }

    [Fact]
    public async Task an_act_that_succeeded_is_not_a_refusal()
    {
        var scenario = newScenario();
        await scenario.WhenActing("nothing happens", _ => Task.CompletedTask);

        var recording = await Recordings.RecordAsync(() =>
        {
            scenario.ThenValidationFails("anything");
            scenario.ThenRefusedWith<WalletFrozen>();
            return Task.CompletedTask;
        });

        recording.Steps.Select(x => x.Status).ShouldBe([ResultStatus.failed, ResultStatus.failed]);
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("but it succeeded");
    }

    [Fact]
    public async Task a_store_only_act_sends_no_messages()
    {
        var scenario = newScenario();
        await scenario.WhenActing("nothing happens", _ => Task.CompletedTask);

        var recording = await Recordings.RecordAsync(() =>
        {
            scenario.ThenMessagesSent<WalletCredited>();
            return Task.CompletedTask;
        });

        recording.Steps.Single().Text.ShouldBe("WalletCredited is sent");
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("but 0 were");
    }

    // ---- a named stream, from a named version (bobcat#461 / wolverine#4938) -------------------

    [Fact]
    public async Task the_stream_gains_only_what_follows_the_named_version()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();

        await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Ann"), new WalletCredited(id, 10));
        (await scenario.TheStreamVersion(id)).ShouldBe(2);

        // Whatever appends next is not this scenario's act, and does not have to be
        await EventStoreAuthoring.AppendAsync(scenario.Store, typeof(Wallet), id, [new WalletCredited(id, 25)]);

        await scenario.ThenEventsOn<Wallet>(id, 2, new WalletCredited(id, 25));
        await scenario.ThenEventsOn<Wallet>(id, 2, Specifications.Specify<WalletCredited>().With(x => x.Amount, 25m));
        await scenario.ThenNoEventsOn<Wallet>(id, 3);

        // Exactly these: a wrong value, a missing event, and an extra one are all wrongs
        await Should.ThrowAsync<SpecAssertionException>(() => scenario.ThenEventsOn<Wallet>(id, 2, new WalletCredited(id, 99)));
        await Should.ThrowAsync<SpecAssertionException>(() => scenario.ThenNoEventsOn<Wallet>(id, 2));
        var extra = await Should.ThrowAsync<SpecAssertionException>(() =>
            scenario.ThenEventsOn<Wallet>(id, 1, new WalletCredited(id, 25)));
        extra.Message.ShouldContain("after version 1");

        // A version past the end of the stream would make every check vacuous
        var ahead = await Should.ThrowAsync<SpecCriticalException>(() => scenario.ThenNoEventsOn<Wallet>(id, 5));
        ahead.Message.ShouldContain("is at version 3");
    }

    [Fact]
    public async Task acting_on_a_stream_reads_as_one_paragraph()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Bea"));
            await scenario.WhenActingOn<Wallet>(id, "the wallet is credited by hand",
                appending(id, new WalletCredited(id, 40)), new WalletCredited(id, 40));

            // The capture feeds the rest of the vocabulary like any other act's
            scenario.ThenEvents(new WalletCredited(id, 40));

            // The undescribed form names the stream instead, and an act that appends nothing passes a check for nothing
            await scenario.WhenActingOn<Wallet>(id, _ => Task.CompletedTask);
        });

        recording.GatheredFailures().ShouldBeNull();
        var lines = recording.Lines().ToList();
        // The paragraph's Given follows the arrange's, so the recorder reads it "And"
        lines[1].ShouldEndWith(" is at version 1");
        lines[1].ShouldStartWith("And the Wallet stream ");
        lines[2].ShouldBe("When the wallet is credited by hand");
        lines[3].ShouldBe("Then WalletCredited is emitted on it");
        lines[4].ShouldBe("And WalletCredited is emitted");
        lines[5].ShouldBe(lines[1].Replace("version 1", "version 2").Replace("And ", "Given "));
        lines[6].ShouldEndWith(" is acted on");
        lines[7].ShouldBe("Then no events are emitted on it");

        scenario.LastAct.Error.ShouldBeNull();
    }

    [Fact]
    public async Task a_wrong_event_fails_only_the_then_sentence()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Cy"));
            await scenario.WhenActingOn<Wallet>(id, appending(id, new WalletCredited(id, 5)), new WalletDebited(id, 5));
        });

        recording.Steps.Select(x => x.Status).ShouldBe(
            [ResultStatus.success, ResultStatus.success, ResultStatus.success, ResultStatus.failed]);
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("after version 1");
    }

    [Fact]
    public async Task a_failing_act_on_a_stream_is_captured_and_reported_by_the_check()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Di"));

        var failed = await Should.ThrowAsync<SpecAssertionException>(() => scenario.WhenActingOn<Wallet>(id,
            _ => throw new InvalidOperationException("The wallet is frozen"),
            new WalletCredited(id, 5)));

        failed.Message.ShouldContain("the act failed");
        failed.Message.ShouldContain("The wallet is frozen");

        // Captured, as any act is: the throwing-refusal step reads it
        scenario.LastAct.Error.ShouldBeOfType<InvalidOperationException>();
        scenario.ThenValidationFails("frozen");
        await Should.ThrowAsync<SpecAssertionException>(() => scenario.ThenNoEventsOn<Wallet>(id, 1));
    }

    // ---- aggregates, documents and read models ------------------------------------------------

    [Fact]
    public async Task an_async_projection_is_read_once_the_daemon_has_caught_up()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Eve"));
            await scenario.WhenActing("the wallet is credited", appending(id, new WalletCredited(id, 3)));

            await scenario.ThenReadModel<WalletSummary>(id, Specifications.Specify<WalletSummary>().With(x => x.Credits, 1));
            await scenario.ThenSingleDocument<WalletSummary>(Specifications.Specify<WalletSummary>().With(x => x.Balance, 3m));
            await scenario.AfterAsyncDaemonsCatchUpAsync();
            await scenario.ThenProjectionsAreCaughtUp(typeof(WalletSummary));
            await scenario.ThenAggregate<Wallet>(id, new Wallet { Id = id, Owner = "Eve", Balance = 3 });
            await scenario.ThenSingleReadModel<Wallet>();
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Lines().ShouldContain("Then the WalletSummary read model has caught up");
        recording.Lines().ShouldContain("Given the async daemons have caught up");
        scenario.ProjectionTimeout.ShouldBe(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task a_missing_aggregate_or_document_is_a_failure_with_nothing_to_hand_back()
    {
        var scenario = newScenario();

        // Outside a recording a wrong is thrown, so a plain test cannot pass by accident
        await Should.ThrowAsync<SpecAssertionException>(() => scenario.ThenAggregate<Wallet>(Guid.NewGuid()));
        await Should.ThrowAsync<SpecAssertionException>(() => scenario.ThenDocument<Customer>(Guid.NewGuid()));
    }

    [Fact]
    public async Task a_derived_scenario_names_the_exception_a_wrong_is_thrown_as()
    {
        Assert.SkipUnless(store.IsAvailable, store.SkipReason);
        var scenario = new OwnExceptionScenario(store.Host);
        await scenario.WhenActing("nothing happens", _ => Task.CompletedTask);

        Should.Throw<OwnSpecificationFailure>(() => scenario.ThenEvents(typeof(WalletCredited)));
        await Should.ThrowAsync<OwnSpecificationFailure>(() => scenario.ThenAggregate<Wallet>(Guid.NewGuid()));
    }

    // ---- DCB tags -----------------------------------------------------------------------------

    [Fact]
    public async Task a_dcb_model_is_projected_over_a_tag_query()
    {
        var scenario = newScenario();
        var screening = new ScreeningId(Guid.NewGuid());

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.GivenEvents<SeatAvailability>(screening.Value,
                Specifications.Tagged(new ScreeningScheduled(screening, 2), screening),
                Specifications.Specify<SeatReserved>().With(x => x.Screening, screening).With(x => x.Seat, "4C").Tagged(screening));

            await scenario.ThenDcbModel<SeatAvailability>(EventTagQuery.For(screening),
                Specifications.Specify<SeatAvailability>().With(x => x.Seats, 1));
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Last().Text.ShouldStartWith("the SeatAvailability over events tagged ");
        (await scenario.TheDcbModel<SeatAvailability>(EventTagQuery.For(screening)))!.Seats.ShouldBe(1);
    }

    [Fact]
    public async Task a_store_only_act_cannot_judge_tags_and_says_why()
    {
        var scenario = newScenario();
        var screening = new ScreeningId(Guid.NewGuid());
        await scenario.GivenEvents<SeatAvailability>(screening, Specifications.Tagged(new ScreeningScheduled(screening, 2), screening));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.WhenActing("a seat is reserved",
                appending(typeof(SeatAvailability), screening.Value, Specifications.Tagged(new SeatReserved(screening, "4C"), screening)));

            // The stream is named, so no tags are needed
            scenario.ThenEventsOn<SeatAvailability>(screening, new SeatReserved(screening, "4C"));
            scenario.ThenEvents(Specifications.Specify<SeatReserved>().With(x => x.Seat, "4C"));
        });
        recording.GatheredFailures().ShouldBeNull();

        // The store appended the events tagged -- the boundary's model sees them -- but reads no tags back
        (await scenario.TheDcbModel<SeatAvailability>(EventTagQuery.For(screening)))!.Seats.ShouldBe(1);
        scenario.LastAct.EventTagsKnown.ShouldBeFalse();

        var tags = await Recordings.RecordAsync(() =>
        {
            scenario.ThenEventsOn(EventTagQuery.For(screening), Specifications.Specify<SeatReserved>());
            scenario.ThenEvents(Specifications.Tagged(new SeatReserved(screening, "4C"), screening));
            return Task.CompletedTask;
        });

        tags.Steps.Select(x => x.Status).ShouldBe([ResultStatus.failed, ResultStatus.failed]);
        tags.GatheredFailures().ShouldNotBeNull().ShouldContain("does not return the tags");
    }

    // ---- any object ---------------------------------------------------------------------------

    [Fact]
    public async Task any_object_is_verified_as_a_table_or_structurally()
    {
        var scenario = newScenario();
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Fay", City = "Austin" };

        var recording = await Recordings.RecordAsync(() =>
        {
            scenario.Verify(customer, StepTable.Parse("| Name | City |\n| Fay | Austin |"));
            scenario.ThenMatches(customer, new Customer { Id = customer.Id, Name = "Fay", City = "Austin" });
            scenario.ThenMatches(customer, Specifications.Specify<Customer>().With(x => x.City, "Austin"));
            return Task.CompletedTask;
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Select(x => x.Text).ShouldBe(["the Customer has", "the Customer matches", "the Customer matches"]);

        Should.Throw<SpecAssertionException>(() => scenario.ThenMatches(customer, Specifications.Specify<Customer>().With(x => x.City, "Boston")));
    }

    // ---- the clock ----------------------------------------------------------------------------

    [Fact]
    public void clock_steps_render_and_move_the_ambient_clock()
    {
        var scenario = newScenario();
        BobcatClock.Reset();

        // Called straight from the test, as a spec calls them: the clock is AsyncLocal, so a value set
        // inside an async helper would never flow back out of it
        var recording = ScenarioRecorder.Begin("CritterStackScenario", "the clock", null, Guid.NewGuid());
        try
        {
            scenario.GivenTheDateIs(new DateOnly(2026, 10, 11));
            scenario.WhenTheClockAdvancesBy(TimeSpan.FromDays(3));
            scenario.WhenTimePasses(TimeSpan.FromHours(1.5));
            scenario.Clock.GetUtcNow().ShouldBe(new DateTimeOffset(2026, 10, 14, 1, 30, 0, TimeSpan.Zero));

            scenario.GivenTheTimeIs(new DateTimeOffset(2026, 1, 2, 9, 15, 0, TimeSpan.Zero));
            scenario.WhenTheClockAdvancesTo(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));
            BobcatClock.Current.GetUtcNow().ShouldBe(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));
        }
        finally
        {
            recording.Dispose();
            BobcatClock.Reset();
        }

        recording.Lines().ShouldBe([
            "Given the date is 2026-10-11",
            "When the clock advances by 3 days",
            "And 1 hour 30 minutes passes",
            "Given the current time is 2026-01-02 09:15",
            "When the clock advances to 2026-01-03"
        ]);
    }

    // ---- run evidence (bobcat#465) ------------------------------------------------------------

    [Fact]
    public async Task what_the_scenario_touched_travels_on_scenario_finished()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        var sink = new RecordingSink();

        await Recordings.RecordAsync(async () =>
        {
            await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Gil"));
            await scenario.WhenActing("the wallet is credited", appending(id, new WalletCredited(id, 2)));

            // Expected, never appended: observed-never-asserted keeps it out
            scenario.ThenNotEmitted<WalletDebited>();
            await scenario.ThenReadModel<WalletSummary>(id);
        }, sink);

        var finished = sink.Events.OfType<ScenarioFinished>().ShouldHaveSingleItem();
        finished.TouchedTypes.ShouldNotBeNull().Select(x => x.Name)
            .ShouldBe(["Wallet", "WalletOpened", "WalletCredited", "WalletSummary"]);
        finished.TouchedTypes![0].FullName.ShouldBe(typeof(Wallet).FullName);
        finished.TouchedTypes![0].AssemblyName.ShouldBe(typeof(Wallet).Assembly.GetName().Name);
    }

    // ---- review findings on bobcat#474 -------------------------------------------------------

    [Fact]
    public async Task a_subclass_of_the_expected_exception_is_a_match()
    {
        var scenario = newScenario();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.WhenActing("the wallet is credited", _ => throw new ArgumentNullException("amount"));
            scenario.ThenRefusedWith<ArgumentException>().ShouldBeOfType<ArgumentNullException>();
        });

        recording.GatheredFailures().ShouldBeNull();
        var cell = recording.Step("refused with ArgumentException").Cells.Single(x => x.Name == "exception");
        cell.Status.ShouldBe(ResultStatus.success);
        cell.Actual.ShouldBe(nameof(ArgumentNullException));
    }

    [Fact]
    public async Task a_step_that_throws_fails_on_its_own_line()
    {
        var scenario = newScenario();

        // A read model that cannot be stored is a SpecCriticalException thrown while its Given is open
        var recording = await Recordings.RecordAsync(async () =>
        {
            await Should.ThrowAsync<SpecCriticalException>(() => scenario.GivenReadModel<Customer>(new Wallet()));
        });

        recording.Steps.Single().Status.ShouldNotBe(ResultStatus.success);
    }

    [Fact]
    public async Task a_store_that_throws_inside_a_step_fails_that_step()
    {
        Assert.SkipUnless(store.IsAvailable, store.SkipReason);

        // An application with no event store at all: the act runs, every store read throws
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var scenario = new CritterStackScenario(
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services));
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.WhenActing("nothing happens", _ => Task.CompletedTask);
            await Should.ThrowAsync<Exception>(() => scenario.ThenStreamIsStarted<Wallet>(id));
            await Should.ThrowAsync<Exception>(() => scenario.AfterAsyncDaemonsCatchUpAsync());
            await Should.ThrowAsync<Exception>(() => scenario.ThenProjectionsAreCaughtUp(typeof(WalletSummary)));
        });

        recording.Steps.Select(x => x.Status == ResultStatus.success).ShouldBe([true, false, false, false]);
    }

    [Fact]
    public async Task a_missing_aggregate_or_read_model_fails_a_rendered_step()
    {
        var scenario = newScenario();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await Should.ThrowAsync<SpecAssertionException>(() => scenario.ThenAggregate<Wallet>(Guid.NewGuid()));
            await Should.ThrowAsync<SpecAssertionException>(() => scenario.ThenReadModel<Customer>(Guid.NewGuid()));
        });

        var steps = recording.Steps.ToList();
        steps.Select(x => x.Status).ShouldBe([ResultStatus.failed, ResultStatus.success, ResultStatus.failed]);
        steps[0].Text.ShouldStartWith("there is a Wallet ");
        steps[1].Text.ShouldBe("the Customer read model has caught up");
        steps[2].Text.ShouldStartWith("there is a Customer ");
    }

    [Fact]
    public async Task a_step_about_the_act_fails_when_no_act_has_run()
    {
        var scenario = newScenario();
        var screening = new ScreeningId(Guid.NewGuid());

        var recording = await Recordings.RecordAsync(() =>
        {
            scenario.ThenNoEvents();
            scenario.ThenNotEmitted<WalletDebited>();
            scenario.ThenNoEventsOn<Wallet>(Guid.NewGuid());
            scenario.ThenNoEventsOn(EventTagQuery.For(screening));
            return Task.CompletedTask;
        });

        recording.Steps.Select(x => x.Status).ShouldAllBe(x => x == ResultStatus.failed);
        recording.Steps.Count.ShouldBe(4);
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("no act has run");
        Should.Throw<SpecAssertionException>(() => scenario.ThenNoEvents()).Message.ShouldContain("no act has run");

        // The refusal half of a guard: an act that threw and appended nothing still passes ThenNoEvents
        await scenario.WhenActing("the debit is refused", _ => throw new WalletFrozen(Guid.NewGuid()));
        scenario.ThenNoEvents();
    }

    [Fact]
    public async Task a_started_stream_answers_only_to_its_own_aggregate_type()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        var screening = new ScreeningId(id);

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.WhenActing("a screening is scheduled",
                appending(typeof(SeatAvailability), id, new ScreeningScheduled(screening, 10)));
            await scenario.ThenStreamIsStartedWithEvents<Wallet>(new ScreeningScheduled(screening, 10));
        });

        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("started no Wallet stream");
        scenario.LastAct.StartedStreams.ShouldHaveSingleItem().AggregateType.ShouldBe(typeof(SeatAvailability));
        scenario.TheStartedStream<SeatAvailability>().ShouldBe(id);
        Should.Throw<SpecCriticalException>(() => scenario.TheStartedStream<Wallet>()).Message.ShouldContain("It started: SeatAvailability");

        // A stream started with no aggregate type still answers to any
        var untyped = Guid.NewGuid();
        await scenario.WhenActing("an untyped stream is started",
            services => EventStoreAuthoring.AppendAsync(services.EventStore(), null!, untyped, [new WalletOpened(untyped, "Jo")]));
        scenario.LastAct.StartedStreams.ShouldHaveSingleItem().AggregateType.ShouldBeNull();
        scenario.TheStartedStream<Wallet>().ShouldBe(untyped);
    }

    [Fact]
    public async Task a_step_that_could_never_fail_is_refused_up_front()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        await scenario.WhenActing("the wallet is credited", _ => throw new WalletFrozen(id));

        Should.Throw<ArgumentOutOfRangeException>(() => scenario.ThenMessagesSent<WalletCredited>(0));
        Should.Throw<ArgumentOutOfRangeException>(() => scenario.ThenMessagesSent<WalletCredited>(-1));
        Should.Throw<ArgumentException>(() => scenario.ThenRefusedWith(""));
        Should.Throw<ArgumentException>(() => scenario.ThenRefusedWith("  ", id));
        Should.Throw<ArgumentException>(() => scenario.ThenValidationFails(" "));
    }

    [Fact]
    public void the_clock_steps_render_what_they_did_in_words_the_grammar_reads_back()
    {
        var scenario = newScenario();
        BobcatClock.Reset();
        var at = new DateTimeOffset(2026, 1, 2, 9, 15, 30, 123, TimeSpan.Zero).AddTicks(4567);

        var recording = ScenarioRecorder.Begin("CritterStackScenario", "the clock, to the tick", null, Guid.NewGuid());
        try
        {
            scenario.GivenTheTimeIs(at);
            scenario.WhenTimePasses(TimeSpan.FromTicks(5_000));
            scenario.WhenTheClockAdvancesBy(TimeSpan.FromTicks(15_002_500));
            scenario.WhenTheClockAdvancesTo(at.AddHours(1));

            // A clock that goes backwards is not one that advances or passes
            Should.Throw<ArgumentOutOfRangeException>(() => scenario.WhenTimePasses(TimeSpan.FromHours(-1)));
            Should.Throw<ArgumentOutOfRangeException>(() => scenario.WhenTheClockAdvancesBy(TimeSpan.FromTicks(-1)));
        }
        finally
        {
            recording.Dispose();
            BobcatClock.Reset();
        }

        recording.Lines().ShouldBe([
            "Given the current time is 2026-01-02 09:15:30.1234567",
            "When 0.5 milliseconds passes",
            "And the clock advances by 1 second 500.25 milliseconds",
            "And the clock advances to 2026-01-02 10:15:30.1234567"
        ]);

        // Each duration reads back through the friendly time-span parser
        FriendlyTimeSpanParser.TryParse("0.5 milliseconds", out var half).ShouldBeTrue();
        half.ShouldBe(TimeSpan.FromTicks(5_000));
        FriendlyTimeSpanParser.TryParse("1 second 500.25 milliseconds", out var longer).ShouldBeTrue();
        longer.ShouldBe(TimeSpan.FromTicks(15_002_500));
    }

    [Fact]
    public async Task the_high_water_sequence_is_the_highest_sequence_in_the_store()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Kai"), new WalletCredited(id, 1));

        var all = await EventStores.QueryEventsSinceAsync(scenario.Store, 0);
        (await EventStores.HighWaterSequenceAsync(scenario.Store)).ShouldBe(all.Max(x => x.Sequence));
    }

    [Fact]
    public async Task a_scenario_can_be_built_from_the_services_alone()
    {
        Assert.SkipUnless(store.IsAvailable, store.SkipReason);
        var scenario = new CritterStackScenario(store.Host.Services);
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.GivenEvents<Wallet>(id, new WalletOpened(id, "Lu"));
            await scenario.WhenActing("the wallet is credited", appending(id, new WalletCredited(id, 8)));
            scenario.ThenEvents(new WalletCredited(id, 8));
            await scenario.ThenAggregate<Wallet>(id, Specifications.Specify<Wallet>().With(x => x.Balance, 8m));
        });

        recording.GatheredFailures().ShouldBeNull();
        scenario.Services.ShouldBeSameAs(store.Host.Services);
        Should.Throw<InvalidOperationException>(() => scenario.Host).Message.ShouldContain("IServiceProvider");
        new CritterStackScenario(store.Host).Services.ShouldBeSameAs(store.Host.Services);
    }

    private sealed class OwnSpecificationFailure(string message) : Exception(message);

    private sealed class OwnExceptionScenario(Microsoft.Extensions.Hosting.IHost host) : CritterStackScenario(host)
    {
        protected override Exception failureOf(string message) => new OwnSpecificationFailure(message);
    }
}

public class critter_stack_scenario_on_marten(MartenStoreHost store)
    : critter_stack_scenario<MartenStoreHost>(store), IClassFixture<MartenStoreHost>;

public class critter_stack_scenario_on_fisher(FisherStoreHost store)
    : critter_stack_scenario<FisherStoreHost>(store), IClassFixture<FisherStoreHost>;

/// <summary>
/// The reset wipes the whole store, so it runs on a host of its own rather than under the other tests'
/// async daemon.
/// </summary>
public abstract class resetting_a_scenario<THost>(THost store) where THost : StoreHost
{
    private static Func<IServiceProvider, Task> appending(Guid id, params object[] events)
        => services => EventStoreAuthoring.AppendAsync(services.EventStore(), typeof(Wallet), id, events);

    private CritterStackScenario newScenario()
    {
        Assert.SkipUnless(store.IsAvailable, store.SkipReason);
        return new CritterStackScenario(store.Host);
    }

    [Fact]
    public async Task reset_forgets_the_last_act()
    {
        var scenario = newScenario();
        var id = Guid.NewGuid();
        await scenario.WhenActing("a wallet is opened", appending(id, new WalletOpened(id, "Hu")));
        scenario.LastAct.NewEvents.ShouldNotBeEmpty();

        await scenario.ResetAsync();

        scenario.LastAct.ShouldBeSameAs(CritterStackActOutcome.None);
        (await scenario.TheStreamVersion(id)).ShouldBe(0);
    }

}

/// <summary>
/// bobcat#497. A read of a projected document is a spec verdict even on a store that holds no events at all — the
/// first test to run against a fresh schema. Marten's own non-stale wait throws an immediate TimeoutException on a
/// store with no events, which made <c>a_missing_aggregate_or_document_is_a_failure_with_nothing_to_hand_back</c>
/// fail whenever xUnit happened to run it first in its class. A class of its own, so its host's store is empty.
/// </summary>
public abstract class reading_an_empty_store<THost>(THost store) where THost : StoreHost
{
    [Fact]
    public async Task a_missing_document_on_a_store_with_no_events_is_a_spec_failure_not_a_timeout()
    {
        Assert.SkipUnless(store.IsAvailable, store.SkipReason);
        var scenario = new CritterStackScenario(store.Host);
        (await EventStores.HighWaterSequenceAsync(scenario.Store)).ShouldBe(0, "this class's store must start empty");

        await Should.ThrowAsync<SpecAssertionException>(() => scenario.ThenDocument<Customer>(Guid.NewGuid()));
        await scenario.ThenNoReadModel<Ghost>();
        await scenario.ThenProjectionsAreCaughtUp(typeof(WalletSummary));
    }
}

public class reading_an_empty_store_on_marten(MartenStoreHost store)
    : reading_an_empty_store<MartenStoreHost>(store), IClassFixture<MartenStoreHost>;

public class reading_an_empty_store_on_fisher(FisherStoreHost store)
    : reading_an_empty_store<FisherStoreHost>(store), IClassFixture<FisherStoreHost>;

/// <summary>
/// bobcat#497, decided as C: a catch-up wait that times out before a read stays an error, but says which wait, which
/// projections were behind, the timeout and what was being read — the store's own exception kept inside.
/// </summary>
public class a_catch_up_timeout_before_a_read
{
    [Fact]
    public async Task is_an_error_that_names_the_wait_the_lagging_projection_the_timeout_and_the_identity()
    {
        var storeTimeout = new TimeoutException("No event activity was detected within the timeout span");
        var database = NSubstitute.Substitute.For<JasperFx.Events.IEventDatabase>();
        NSubstitute.SubstituteExtensions.Returns(database.WaitForNonStaleProjectionDataAsync(NSubstitute.Arg.Any<TimeSpan>()),
            Task.FromException(storeTimeout));
        NSubstitute.SubstituteExtensions.Returns(database.FetchHighestEventSequenceNumber(NSubstitute.Arg.Any<CancellationToken>()),
            Task.FromResult(12L));
        NSubstitute.SubstituteExtensions.Returns(database.AllProjectionProgress(NSubstitute.Arg.Any<CancellationToken>()),
            Task.FromResult<IReadOnlyList<JasperFx.Events.Projections.ShardState>>(
            [
                new(JasperFx.Events.Projections.ShardState.HighWaterMark, 12),
                new("WalletSummary:All", 7),
                new("Ledger:All", 12)
            ]));

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<JasperFx.Events.IEventStore>(
            services, new FakeEventStore { Databases = [database] });
        var scenario = new CritterStackScenario(
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services))
        {
            ProjectionTimeout = TimeSpan.FromSeconds(3)
        };
        var id = Guid.NewGuid();

        // An error, not a spec failure: never a SpecAssertionException, gathered or thrown
        var ex = await Should.ThrowAsync<ProjectionCatchUpTimeoutException>(() => scenario.ThenDocument<Customer>(id));

        ex.InnerException.ShouldBeSameAs(storeTimeout);
        ex.ShouldBeAssignableTo<TimeoutException>();
        ex.Wait.ShouldBe("the Customer read model has caught up");
        ex.Behind.ShouldBe(["'WalletSummary:All' at 7 of 12"]);
        ex.Timeout.ShouldBe(TimeSpan.FromSeconds(3));
        ex.ReadType.ShouldBe(typeof(Customer));
        ex.Identity.ShouldBe(id);
        ex.Message.ShouldContain("'the Customer read model has caught up' timed out after 3s");
        ex.Message.ShouldContain($"the Customer with id {id}");
        ex.Message.ShouldContain("Behind: 'WalletSummary:All' at 7 of 12.");
        ex.Message.ShouldContain("No event activity was detected");

        // Inside a recording the step that waited fails on its own line, with the same exception
        var recording = await Recordings.RecordAsync(async () =>
            await Should.ThrowAsync<ProjectionCatchUpTimeoutException>(() => scenario.ThenReadModel<Customer>(id)));
        // ...at the error level: the environment, not a wrong
        recording.Steps.Single().Text.ShouldBe("the Customer read model has caught up");
        recording.Steps.Single().Status.ShouldBe(ResultStatus.error);
    }
}

public class resetting_a_scenario_on_marten(MartenStoreHost store)
    : resetting_a_scenario<MartenStoreHost>(store), IClassFixture<MartenStoreHost>;

public class resetting_a_scenario_on_fisher(FisherStoreHost store)
    : resetting_a_scenario<FisherStoreHost>(store), IClassFixture<FisherStoreHost>;
