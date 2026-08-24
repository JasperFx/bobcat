using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #146 — the worker's pid surfaced to consumers, so external diagnostics (a dump, a
/// stack capture, an RSS sample) can be pointed at the process the supervisor is driving
/// instead of guessing which process on the box is the test host.
/// </summary>
public class WorkerProcessIdTests
{
    private sealed class RecordingObserver : ISupervisorObserver
    {
        public List<WorkerLaunchContext> Started { get; } = [];
        public List<(WorkerLaunchContext Worker, WorkerTestUpdate Update)> Updates { get; } = [];
        public List<WorkerFault> Faults { get; } = [];

        public void WorkerStarted(WorkerLaunchContext worker)
        {
            lock (Started) Started.Add(worker);
        }

        public void TestUpdated(WorkerLaunchContext worker, WorkerTestUpdate update)
        {
            lock (Updates) Updates.Add((worker, update));
        }

        public void WorkerFaulted(WorkerFault fault)
        {
            lock (Faults) Faults.Add(fault);
        }
    }

    [Fact]
    public void a_client_that_does_not_drive_a_process_has_no_pid()
    {
        // The default member: an in-process client never has to think about pids.
        IWorkerClient client = new InProcessClient();
        client.ProcessId.ShouldBeNull();
    }

    [Fact]
    public async Task every_launch_is_announced_with_the_pid_stamped_on_its_context()
    {
        var observer = new RecordingObserver();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            ProcessIdFor = index => 4200 + index
        };

        var supervisor = new Supervisor(factory);
        supervisor.AddObserver(observer);

        await supervisor.Run();

        // Discovery included: it never reports test progress, but it is still a process
        // someone may need to diagnose.
        observer.Started.Count.ShouldBe(2);
        observer.Started[0].Purpose.ShouldBe(WorkerPurpose.Discovery);
        observer.Started[0].ProcessId.ShouldBe(4200);
        observer.Started[1].Purpose.ShouldBe(WorkerPurpose.Lane);
        observer.Started[1].ProcessId.ShouldBe(4201);
    }

    [Fact]
    public async Task test_updates_carry_the_pid_of_the_worker_that_reported_them()
    {
        // The lane-to-pid correlation the wolverine watchdog had to infer from /proc — and got
        // wrong, latching onto a database container instead of the test host.
        var observer = new RecordingObserver();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            ProcessIdFor = index => 4200 + index
        };

        var supervisor = new Supervisor(factory);
        supervisor.AddObserver(observer);

        await supervisor.Run();

        observer.Updates.ShouldNotBeEmpty();
        observer.Updates.ShouldAllBe(u => u.Worker.ProcessId == 4201);
    }

    [Fact]
    public async Task a_worker_fault_names_the_process_that_died()
    {
        var observer = new RecordingObserver();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => null,
            Fault = w => w.Runs.Count > 0 ? "the worker exited with code 139" : null,
            FaultExitCode = 139,
            ProcessIdFor = index => 4200 + index
        };

        var supervisor = new Supervisor(factory);
        supervisor.AddObserver(observer);

        await supervisor.Run();

        var fault = observer.Faults.ShouldHaveSingleItem();
        fault.ExitCode.ShouldBe(139);
        fault.ProcessId.ShouldBe(4201);
    }

    [Fact]
    public async Task an_in_process_worker_is_still_announced_with_a_null_pid()
    {
        // Nullable is the contract, not an error state: not every IWorkerClient need be a
        // separate process, and a consumer filters rather than the supervisor pretending.
        var observer = new RecordingObserver();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory);
        supervisor.AddObserver(observer);

        await supervisor.Run();

        observer.Started.Count.ShouldBe(2);
        observer.Started.ShouldAllBe(w => w.ProcessId == null);
    }

    private sealed class InProcessClient : IWorkerClient
    {
        public Task<IReadOnlyList<WorkerTest>> Discover(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkerTest>>([]);

        public Task<WorkerRunResult> Run(IReadOnlyList<string>? uids = null, CancellationToken ct = default)
            => Task.FromResult(new WorkerRunResult([]));

        public ValueTask DisposeAsync() => default;
    }
}
