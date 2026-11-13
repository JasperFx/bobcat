using Bobcat.Monitoring;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #195 — the supervisor forwarding its workers' live per-test stream to the monitor.
/// A plain xUnit worker has no <c>MonitorPublishingObserver</c>, so before this a supervised
/// suite of 1627 tests registered on the dashboard with the right total and then sat at zero
/// finished for the whole five-minute run: a card indistinguishable from a wedged one.
/// </summary>
public class ForeignTestProgressPublishingTests
{
    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = [];

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToArray(); }
        }
    }

    private static Supervisor supervised(FakeWorkerFactory factory, RecordingSink sink)
        => new(factory) { PublishToMonitor = true, MonitorSink = sink };

    [Fact]
    public async Task every_test_reaches_the_wire_as_a_start_and_a_verdict()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one"), FakeWorkerFactory.Test("A.two")],
            Outcome = (uid, _, _) => uid == "A.two" ? WorkerTestState.Failed : WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        var runId = sink.Events.OfType<RunStarted>().Single().RunId;

        sink.Events.OfType<TestStarted>().Select(e => e.Uid).OrderBy(u => u).ShouldBe(["A.one", "A.two"]);
        sink.Events.OfType<TestStarted>().ShouldAllBe(e => e.RunId == runId && e.Lane == 0);

        var finished = sink.Events.OfType<TestFinished>().ToDictionary(e => e.Uid);
        finished["A.one"].State.ShouldBe("Passed");
        // The framework's own word, not a re-label — "skipped" is a fact RunOutcome has no word
        // for, and mapping at the publisher is how two vocabularies quietly drift apart.
        finished["A.two"].State.ShouldBe("Failed");
        finished.Values.ShouldAllBe(e => e.RunId == runId);
    }

    [Fact]
    public async Task a_verdict_carries_the_duration_the_supervisor_measured_between_the_two_updates()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        // Measured on the supervisor's clock, which is the only one both ends of the transition
        // share. It is a real elapsed time, so the assertion is that it exists and is sane.
        var finished = sink.Events.OfType<TestFinished>().ShouldHaveSingleItem();
        finished.DurationMs.ShouldNotBeNull();
        finished.DurationMs.Value.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task the_start_and_the_verdict_are_ordered_inside_the_run_bracket()
    {
        // run_started stays the stream's first event and run_finished its last — the one
        // ordering every consumer may assume.
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        var kinds = sink.Events.Select(e => e.GetType()).ToList();
        kinds[0].ShouldBe(typeof(RunStarted));
        kinds[^1].ShouldBe(typeof(RunFinished));
        kinds.IndexOf(typeof(TestStarted)).ShouldBeLessThan(kinds.IndexOf(typeof(TestFinished)));
    }

    [Fact]
    public async Task a_discovery_worker_puts_nothing_on_the_wire()
    {
        // Discovery enumerates; "discovered" is not progress, and the supervisor never taps it.
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        sink.Events.OfType<TestStarted>().Count().ShouldBe(1);
        sink.Events.OfType<TestFinished>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task a_test_a_crashed_worker_never_answered_for_produces_no_verdict_on_the_wire()
    {
        // Its outcome is padded to Indeterminate for the report, and that is absence of
        // evidence: it must never move a progress bar. Nothing is published for it at all,
        // rather than a "finished" that would make a crashed run read as a complete one.
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one"), FakeWorkerFactory.Test("A.two")],
            Outcome = (uid, _, _) => uid == "A.one" ? WorkerTestState.Passed : null,
            Fault = _ => "the worker process exited with code 134"
        };

        await supervised(factory, sink).Run();

        sink.Events.OfType<TestFinished>().Select(e => e.Uid).ShouldBe(["A.one"]);
        // The start still travelled — the supervisor did watch it begin.
        sink.Events.OfType<TestStarted>().Select(e => e.Uid).OrderBy(u => u).ShouldBe(["A.one", "A.two"]);
    }

    [Fact]
    public async Task an_isolated_test_reports_no_lane_because_its_process_occupies_no_slot()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.alone", "Isolated"), FakeWorkerFactory.Test("A.batched")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        sink.Events.OfType<TestFinished>().Single(e => e.Uid == "A.alone").Lane.ShouldBeNull();
        sink.Events.OfType<TestFinished>().Single(e => e.Uid == "A.batched").Lane.ShouldBe(0);
    }

    // Issue #515: the spec identity beside the uid, so a console folds the two per-test streams
    // exactly instead of matching them by method name.

    private static SpecManifest manifestOf(params (string Identity, string Class, string Method)[] specs)
        => new(SpecManifest.ProjectedLane, SpecManifest.XunitFramework, "Orders.Tests",
            specs.Select(s => new SpecManifestEntry(s.Identity, s.Class, s.Method)).ToArray());

    [Fact]
    public async Task a_forwarded_test_carries_the_specification_identity_the_manifest_names()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.InClass("Orders.Tests.OrderSpecs", "places_an_order")],
            Manifest = manifestOf(("Orders/places an order", "Orders.Tests.OrderSpecs", "places_an_order")),
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        // The uid keeps its documented meaning -- the platform's own id -- and the identity rides
        // beside it, the same string the worker's own scenario_* events carry.
        var started = sink.Events.OfType<TestStarted>().ShouldHaveSingleItem();
        started.Uid.ShouldBe("Orders.Tests.OrderSpecs.places_an_order");
        started.SpecId.ShouldBe("Orders/places an order");

        sink.Events.OfType<TestFinished>().ShouldHaveSingleItem().SpecId.ShouldBe("Orders/places an order");
    }

    [Fact]
    public async Task an_opaque_uid_is_named_through_the_reported_location()
    {
        // The case that matters: xUnit's real uid is a hash, so the display name is no help and
        // the discovery location is what places the test. Same join the resident runner uses.
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests =
            [
                new WorkerTest("9F2C51A7", "places an order")
                {
                    TestClass = "Orders.Tests.OrderSpecs",
                    TestMethod = "places_an_order"
                }
            ],
            Manifest = manifestOf(("Orders/places an order", "Orders.Tests.OrderSpecs", "places_an_order")),
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        sink.Events.OfType<TestFinished>().ShouldHaveSingleItem().SpecId.ShouldBe("Orders/places an order");
    }

    [Fact]
    public async Task a_test_no_manifest_names_carries_no_identity_at_all()
    {
        // A plain xUnit suite -- the #195 case -- writes no manifest, and a suite that writes one
        // need not have bound every test in it. Null rather than a guess: the field exists to
        // make a fold exact, and an invented identity would fold two tests into one card.
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests =
            [
                FakeWorkerFactory.InClass("Orders.Tests.OrderSpecs", "places_an_order"),
                FakeWorkerFactory.InClass("Orders.Tests.PlainTests", "an_ordinary_unit_test")
            ],
            Manifest = manifestOf(("Orders/places an order", "Orders.Tests.OrderSpecs", "places_an_order")),
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        var finished = sink.Events.OfType<TestFinished>().ToDictionary(e => e.Uid);
        finished["Orders.Tests.OrderSpecs.places_an_order"].SpecId.ShouldBe("Orders/places an order");
        finished["Orders.Tests.PlainTests.an_ordinary_unit_test"].SpecId.ShouldBeNull();
    }

    [Fact]
    public async Task an_identity_two_tests_claim_names_neither_of_them()
    {
        // bobcat#489's duplicate. The manifest keeps the first claimant as its one entry, so
        // stamping it would say "this is the specification" about one of two tests that both are,
        // and a fold keyed on it would attach the other's scenario to the wrong card.
        var sink = new RecordingSink();
        var duplicate = new SpecManifest(
            SpecManifest.ProjectedLane, SpecManifest.XunitFramework, "Orders.Tests",
            [new SpecManifestEntry("Orders/places an order", "Orders.Tests.OrderSpecs", "places_an_order")])
        {
            Duplicates =
            [
                new SpecManifestDuplicate("Orders/places an order",
                [
                    new SpecManifestEntry("Orders/places an order", "Orders.Tests.OrderSpecs", "places_an_order"),
                    new SpecManifestEntry("Orders/places an order", "Orders.Tests.OtherSpecs", "places_an_order")
                ])
            ]
        };

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.InClass("Orders.Tests.OrderSpecs", "places_an_order")],
            Manifest = duplicate,
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        sink.Events.OfType<TestFinished>().ShouldHaveSingleItem().SpecId.ShouldBeNull();
    }

    [Fact]
    public async Task a_supervised_failure_never_reaches_the_wire_without_its_reason()
    {
        // bobcat#825. The Marten gate's 232 LinqTests failures arrived as "Failed" and nothing
        // else — five distinct clusters a person had to rerun by hand to tell apart — because the
        // supervisor had the worker's message in its WorkerOutcome and test_finished had no field
        // for it. A Bobcat spec's own scenario_finished always carried its message; only this
        // forwarded stream dropped it.
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.passes"), FakeWorkerFactory.Test("A.fails"), FakeWorkerFactory.Test("A.errors")],
            Outcome = (uid, _, _) => uid switch
            {
                "A.passes" => WorkerTestState.Passed,
                "A.fails" => WorkerTestState.Failed,
                _ => WorkerTestState.Error
            },
            ErrorType = (uid, _) => uid == "A.fails" ? "Shouldly.ShouldAssertException" : null,
            ErrorMessageFor = (uid, _) => uid == "A.fails"
                ? "Shouldly.ShouldAssertException : total should be 6 but was 8"
                : "System.InvalidOperationException : the fixture never started",
            // tUnit sends no stack at all; the message still travels on its own.
            StackTraceFor = (uid, _) => uid == "A.fails" ? "   at Acme.OrderTests.pays() in OrderTests.cs:line 42" : null
        };

        await supervised(factory, sink).Run();

        var finished = sink.Events.OfType<TestFinished>().ToDictionary(e => e.Uid);

        finished["A.fails"].State.ShouldBe("Failed");
        finished["A.fails"].ErrorMessage.ShouldBe("Shouldly.ShouldAssertException : total should be 6 but was 8");
        finished["A.fails"].StackTrace.ShouldBe("   at Acme.OrderTests.pays() in OrderTests.cs:line 42");

        finished["A.errors"].State.ShouldBe("Error");
        finished["A.errors"].ErrorMessage.ShouldBe("System.InvalidOperationException : the fixture never started");
        finished["A.errors"].StackTrace.ShouldBeNull();

        // The refusal in the name: no non-passing verdict on the wire without a reason.
        sink.Events.OfType<TestFinished>()
            .Where(e => e.State is not ("Passed" or "Skipped"))
            .ShouldAllBe(e => e.ErrorMessage != null);

        // And a pass carries none — the field is a reason, not a slot to fill.
        finished["A.passes"].ErrorMessage.ShouldBeNull();
        finished["A.passes"].StackTrace.ShouldBeNull();
    }

    [Fact]
    public async Task a_long_stack_trace_is_capped_on_the_wire_and_says_so()
    {
        // One fixture failing to start fails every test in its class with the same hundred-frame
        // trace; a hundred such tests must not make the reason the bulk of the run's ndjson. The
        // cut keeps the top of the trace — the throw site — and never happens silently.
        var trace = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"   at Acme.Fixture.Frame{i}() in Fixture.cs:line {i}"));
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.fails")],
            Outcome = (_, _, _) => WorkerTestState.Failed,
            StackTraceFor = (_, _) => trace
        };

        await supervised(factory, sink).Run();

        var finished = sink.Events.OfType<TestFinished>().ShouldHaveSingleItem();
        var lines = finished.StackTrace.ShouldNotBeNull().Split('\n');

        lines.Length.ShouldBe(WireStackTrace.MaxLines + 1);
        lines[0].ShouldContain("Frame1()");
        lines[WireStackTrace.MaxLines - 1].ShouldContain($"Frame{WireStackTrace.MaxLines}()");
        lines[^1].ShouldBe(WireStackTrace.TruncationMarker);
        finished.StackTrace.ShouldBe(WireStackTrace.Cap(trace));
    }
}
