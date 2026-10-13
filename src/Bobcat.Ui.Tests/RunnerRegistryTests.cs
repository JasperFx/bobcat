using Shouldly;
using Bobcat.Ui.Runners;

namespace Bobcat.Ui.Tests;

/// <summary>
/// stoat#77: the runner registry's delivery rules. A command must reach a runner exactly when it
/// should — never lost to a dropped stream, never repeated to a runner that already acknowledged it.
/// </summary>
public class RunnerRegistryTests
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(5);

    private static RunnerRegistered registration(string id = "r-1", params string[] modes)
        => new(id, "/code/stoat", "main", "Stoat.Specs", "gherkin", modes.Length > 0 ? modes : null, ["F/a", "F/b", "G/c"]);

    private static RunnerRegistry registry()
    {
        var runners = new RunnerRegistry();
        runners.Register(registration(), "JasperFx/stoat");
        return runners;
    }

    /// <summary>Read the next <paramref name="count"/> items from a stream, then drop it — what a
    /// connection that closes looks like.</summary>
    private static async Task<List<object?>> take(RunnerRegistry runners, int count, Guid? lastEventId = null, TimeSpan? keepAlive = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var items = new List<object?>();
        await foreach (var item in runners.Stream("r-1", lastEventId, keepAlive ?? Long, cts.Token))
        {
            items.Add(item);
            if (items.Count == count) break;
        }

        return items;
    }

    private static Guid idOf(object? command) => command switch
    {
        RunSpecsCommand run => run.CommandId,
        RestartRunnerCommand restart => restart.CommandId,
        _ => throw new InvalidOperationException()
    };

    [Fact]
    public async Task one_command_is_in_flight_at_a_time_and_the_next_waits_for_its_answer()
    {
        // Bobcat's runner refuses a second command as busy rather than queueing it (bobcat#390), so
        // the queue is Stoat's: sending both at once would have the second refused every time.
        var runners = registry();
        var first = runners.Enqueue("r-1", "run", ["F/a"], RunnerWire.Cold, "jeremy");
        var second = runners.Enqueue("r-1", "restart", [], RunnerWire.Cold, "jeremy");

        var items = await take(runners, 2, keepAlive: TimeSpan.FromMilliseconds(100));

        idOf(items[0]).ShouldBe(first);
        items[0].ShouldBeOfType<RunSpecsCommand>().Specs.ShouldBe(["F/a"]);
        items[1].ShouldBeNull(); // a keep-alive: the second waits
        runners.Find("r-1")!.Commands.Single(c => c.CommandId == second).State.ShouldBe("queued");

        runners.Acknowledge("r-1", first, accepted: true, reason: null);
        (await take(runners, 1)).Single().ShouldBeOfType<RestartRunnerCommand>().CommandId.ShouldBe(second);
    }

    [Fact]
    public async Task a_command_queued_while_the_stream_waits_is_sent_at_once()
    {
        var runners = registry();
        var reading = take(runners, 1);
        await Task.Delay(50);

        var command = runners.Enqueue("r-1", "run", ["F/b"], RunnerWire.Cold, "jeremy");

        idOf((await reading).Single()).ShouldBe(command);
    }

    [Fact]
    public async Task an_acknowledged_command_is_never_sent_again()
    {
        var runners = registry();
        var command = runners.Enqueue("r-1", "run", ["F/a"], RunnerWire.Cold, "jeremy");
        await take(runners, 1);

        runners.Acknowledge("r-1", command, accepted: true, reason: null).ShouldBeTrue();

        // A fresh connection gets nothing but the keep-alive.
        (await take(runners, 1, keepAlive: TimeSpan.FromMilliseconds(50))).Single().ShouldBeNull();
        runners.Find("r-1")!.Commands.Single().State.ShouldBe("accepted");
    }

    [Fact]
    public async Task a_new_process_gets_every_command_still_unacknowledged_so_none_is_lost()
    {
        // The runner received it, then died before acknowledging. Its replacement starts a new
        // stream with no Last-Event-ID, and must get the command again.
        var runners = registry();
        var command = runners.Enqueue("r-1", "run", ["F/a"], RunnerWire.Cold, "jeremy");
        await take(runners, 1);

        idOf((await take(runners, 1)).Single()).ShouldBe(command);
    }

    [Fact]
    public async Task a_reconnect_with_last_event_id_does_not_repeat_the_command_in_flight()
    {
        // The same process lost its connection while running the first command, which it has.
        var runners = registry();
        var first = runners.Enqueue("r-1", "run", ["F/a"], RunnerWire.Cold, "jeremy");
        await take(runners, 1);
        var second = runners.Enqueue("r-1", "run", ["F/b"], RunnerWire.Cold, "jeremy");

        // Nothing new for it while the first is unanswered — only a keep-alive.
        (await take(runners, 1, lastEventId: first, keepAlive: TimeSpan.FromMilliseconds(50))).Single().ShouldBeNull();

        // And once it answers, the second.
        runners.Acknowledge("r-1", first, accepted: true, reason: null);
        idOf((await take(runners, 1, lastEventId: first)).Single()).ShouldBe(second);
    }

    [Fact]
    public async Task a_command_refused_as_busy_waits_its_turn_and_is_sent_again()
    {
        // run_finished arrives a hair before the runner is free (bobcat 5066347), so "busy" is an
        // ordinary answer to a command sent right after a run, and the right response is to resend.
        var runners = new RunnerRegistry(busyRetry: TimeSpan.FromMilliseconds(100));
        runners.Register(registration(), "JasperFx/stoat");
        var command = runners.Enqueue("r-1", "run", ["F/a"], RunnerWire.Cold, "jeremy");
        await take(runners, 1);

        runners.Acknowledge("r-1", command, accepted: false, reason: "this runner is already running a command").ShouldBeTrue();
        runners.Find("r-1")!.Commands.Single().State.ShouldBe("queued");

        idOf((await take(runners, 1)).Single()).ShouldBe(command);
    }

    [Fact]
    public async Task an_acceptance_is_never_undone_by_a_busy_answer_to_the_same_command_resent()
    {
        // A runner refuses a resent command it already accepted as busy — it is busy running it.
        var runners = registry();
        var command = runners.Enqueue("r-1", "run", ["F/a"], RunnerWire.Cold, "jeremy");
        await take(runners, 1);
        runners.Acknowledge("r-1", command, accepted: true, reason: null);

        runners.Acknowledge("r-1", command, accepted: false, reason: "this runner is already running a command");

        runners.Find("r-1")!.Commands.Single().State.ShouldBe("accepted");
    }

    [Fact]
    public void the_refusal_kind_decides_and_the_sentence_is_only_for_a_runner_that_predates_it()
    {
        // bobcat#400: the kind is the contract; a rewording of the sentence must change nothing.
        RunnerRegistry.IsBusy("busy", "the runner is occupied").ShouldBeTrue();
        RunnerRegistry.IsBusy("unknown-spec", "busy is not a spec I have").ShouldBeFalse();
        RunnerRegistry.IsBusy(null, "this runner is already running a command").ShouldBeTrue(); // 0.29.0
        RunnerRegistry.IsBusy(null, "not this runner's specs: X/y").ShouldBeFalse();
    }

    [Fact]
    public void a_refusal_that_is_not_busy_settles_the_command()
    {
        var runners = registry();
        var command = runners.Enqueue("r-1", "run", ["F/a"], RunnerWire.Cold, "jeremy");

        runners.Acknowledge("r-1", command, accepted: false, reason: "not this runner's specs: X/y");

        var view = runners.Find("r-1")!.Commands.Single();
        view.State.ShouldBe("rejected");
        view.Reason.ShouldBe("not this runner's specs: X/y");
    }

    [Fact]
    public async Task a_restarted_runner_from_the_same_checkout_takes_over_the_queue_of_the_one_that_is_gone()
    {
        // Bobcat 0.29.0 mints a new runner id on every start. A command pressed while the runner
        // was rebuilding must reach the process that comes back, not wait for an id that never will.
        var runners = registry();
        var pressed = runners.Enqueue("r-1", "run", ["F/a"], RunnerWire.Cold, "jeremy");

        runners.Register(registration("r-2"), "JasperFx/stoat");

        runners.All().Select(x => x.RunnerId).ShouldBe(["r-2"]);
        idOf((await runners.Stream("r-2", null, Long, TestContext.Current.CancellationToken).FirstAsync())).ShouldBe(pressed);

        // A button following the command under the old id still finds it.
        runners.Find("r-1")!.RunnerId.ShouldBe("r-2");
        runners.Acknowledge("r-1", pressed, true, null).ShouldBeTrue();
    }

    [Fact]
    public async Task a_runner_that_is_still_listening_is_never_superseded()
    {
        // Two live processes for one checkout is someone's choice; Stoat does not pick a winner.
        var runners = registry();
        using var cts = new CancellationTokenSource();
        var listening = runners.Stream("r-1", null, Long, cts.Token).GetAsyncEnumerator(cts.Token);
        var moving = listening.MoveNextAsync();
        await Task.Delay(50);

        runners.Register(registration("r-2"), "JasperFx/stoat");
        runners.All().Select(x => x.RunnerId).ShouldBe(["r-1", "r-2"], ignoreOrder: true);

        cts.Cancel();
        try { await moving; } catch (OperationCanceledException) { }
        await listening.DisposeAsync();
    }

    [Fact]
    public async Task an_idle_stream_sends_keep_alives()
    {
        var items = await take(registry(), 2, keepAlive: TimeSpan.FromMilliseconds(30));
        items.ShouldAllBe(x => x == null);
    }

    [Fact]
    public async Task a_runner_is_connected_only_while_its_stream_is_open()
    {
        var runners = registry();
        runners.Find("r-1")!.Connected.ShouldBeFalse();

        using var cts = new CancellationTokenSource();
        var enumerator = runners.Stream("r-1", null, Long, cts.Token).GetAsyncEnumerator(cts.Token);
        var moving = enumerator.MoveNextAsync();
        await Task.Delay(50);
        runners.Find("r-1")!.Connected.ShouldBeTrue();

        cts.Cancel();
        try { await moving; } catch (OperationCanceledException) { }
        await enumerator.DisposeAsync();
        runners.Find("r-1")!.Connected.ShouldBeFalse();
    }

    [Fact]
    public void registering_again_keeps_the_queue_and_ends_a_rebuilding_status()
    {
        var runners = registry();
        runners.Enqueue("r-1", "run", ["F/a"], RunnerWire.Cold, "jeremy");
        runners.ReportStatus("r-1", RunnerWire.Rebuilding, null);

        runners.Register(registration(), "JasperFx/stoat");

        var view = runners.Find("r-1")!;
        view.Commands.ShouldHaveSingleItem().State.ShouldBe("queued");
        view.Status.ShouldBeNull();
    }

    [Fact]
    public void a_runner_that_named_no_modes_offers_cold_only()
    {
        registry().ModesOf("r-1").ShouldBe([RunnerWire.Cold]);

        var warm = new RunnerRegistry();
        warm.Register(registration("r-1", RunnerWire.Cold, RunnerWire.Warm), null);
        warm.ModesOf("r-1").ShouldBe([RunnerWire.Cold, RunnerWire.Warm]);
    }

    [Fact]
    public void an_acknowledgement_for_a_command_stoat_never_sent_is_ignored()
    {
        registry().Acknowledge("r-1", Guid.NewGuid(), true, null).ShouldBeFalse();
        registry().Acknowledge("nobody", Guid.NewGuid(), true, null).ShouldBeFalse();
    }

    [Fact]
    public void a_runner_is_a_candidate_only_when_it_registered_every_spec_asked_for()
    {
        // stoat#78's picker: offering a runner that lacks one of the specs would end in a refusal.
        var runners = registry();
        runners.Register(new RunnerRegistered("r-2", "/code/other", "main", "Other.Specs", "gherkin", null, ["F/a", "H/d"]), null);

        runners.CandidatesFor(["F/a"], null).Select(x => x.RunnerId).ShouldBe(["r-1", "r-2"], ignoreOrder: true);
        runners.CandidatesFor(["F/a", "F/b"], null).Select(x => x.RunnerId).ShouldBe(["r-1"]);
        runners.CandidatesFor(["Z/z"], null).ShouldBeEmpty();
        runners.CandidatesFor([], null).ShouldBeEmpty();

        // A feature needs only one spec in it.
        runners.CandidatesFor(null, "H").Select(x => x.RunnerId).ShouldBe(["r-2"]);
        runners.CandidatesFor(null, "F").Count.ShouldBe(2);
    }

    [Fact]
    public async Task a_listening_runner_is_offered_before_one_that_is_not()
    {
        var runners = registry();
        runners.Register(new RunnerRegistered("r-2", "/code/other", "main", "Other.Specs", "gherkin", null, ["F/a"]), null);

        using var cts = new CancellationTokenSource();
        var listening = runners.Stream("r-2", null, Long, cts.Token).GetAsyncEnumerator(cts.Token);
        var moving = listening.MoveNextAsync();
        await Task.Delay(50);

        runners.CandidatesFor(["F/a"], null).Select(x => x.RunnerId).ShouldBe(["r-2", "r-1"]);

        cts.Cancel();
        try { await moving; } catch (OperationCanceledException) { }
        await listening.DisposeAsync();
    }

    [Fact]
    public void only_the_latest_settled_commands_are_kept_and_pending_ones_never_trimmed()
    {
        var runners = registry();
        for (var i = 0; i < 30; i++)
        {
            var id = runners.Enqueue("r-1", "run", ["F/a"], RunnerWire.Cold, "jeremy");
            runners.Acknowledge("r-1", id, true, null);
        }

        var pending = runners.Enqueue("r-1", "run", ["F/b"], RunnerWire.Cold, "jeremy");

        var commands = runners.Find("r-1")!.Commands;
        commands.Count(c => c.State == "accepted").ShouldBe(20);
        commands.ShouldContain(c => c.CommandId == pending && c.State == "queued");
    }
}
