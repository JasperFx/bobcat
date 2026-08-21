using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #99 — the tap on MTP's live <c>testing/testUpdates/tests</c> stream, which
/// <see cref="MtpWorkerClient"/> used to read for outcomes and otherwise discard. The fake
/// worker proves the supervisor relays what a client reports, stamped with the launch it came
/// from; the real worker proves an actual MTP host reports a test in progress before its
/// verdict.
/// </summary>
public class TestUpdateTapTests
{
    private sealed class RecordingObserver : ISupervisorObserver
    {
        public List<(WorkerLaunchContext Worker, WorkerTestUpdate Update)> Updates { get; } = [];

        public void TestUpdated(WorkerLaunchContext worker, WorkerTestUpdate update)
        {
            lock (Updates) Updates.Add((worker, update));
        }
    }

    [Fact]
    public async Task in_progress_and_terminal_updates_reach_the_observer_with_the_lane()
    {
        var observer = new RecordingObserver();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one"), FakeWorkerFactory.Test("A.two")],
            Outcome = (uid, _, _) => uid == "A.two" ? WorkerTestState.Failed : WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory);
        supervisor.AddObserver(observer);

        await supervisor.Run();

        // Each test: in progress first, then its verdict.
        var one = observer.Updates.Where(u => u.Update.Uid == "A.one").Select(u => u.Update).ToList();
        one.Select(u => u.InProgress).ShouldBe([true, false]);
        one[0].State.ShouldBeNull();
        one[1].State.ShouldBe(WorkerTestState.Passed);

        var two = observer.Updates.Where(u => u.Update.Uid == "A.two").Select(u => u.Update).ToList();
        two.Select(u => u.InProgress).ShouldBe([true, false]);
        two[1].State.ShouldBe(WorkerTestState.Failed);

        // Stamped with the launch: a lane of the pool, not the discovery worker.
        observer.Updates.ShouldAllBe(u => u.Worker.Purpose == WorkerPurpose.Lane && u.Worker.Lane == 0);
    }

    [Fact]
    public async Task the_discovery_worker_is_not_tapped()
    {
        // Discovery enumerates; "discovered" is not progress, and a dashboard that showed every
        // test flicker at the start of a run would be reporting nothing.
        var observer = new RecordingObserver();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory);
        supervisor.AddObserver(observer);

        await supervisor.Run();

        observer.Updates.ShouldAllBe(u => u.Worker.Purpose != WorkerPurpose.Discovery);
        observer.Updates.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task an_isolated_test_reports_from_its_own_dedicated_launch()
    {
        var observer = new RecordingObserver();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.alone", "Isolated"), FakeWorkerFactory.Test("A.batched")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory);
        supervisor.AddObserver(observer);

        await supervisor.Run();

        observer.Updates.Where(u => u.Update.Uid == "A.alone")
            .ShouldAllBe(u => u.Worker.Purpose == WorkerPurpose.Isolated);
        observer.Updates.Where(u => u.Update.Uid == "A.batched")
            .ShouldAllBe(u => u.Worker.Purpose == WorkerPurpose.Lane);
    }

    [Fact]
    public async Task a_throwing_observer_does_not_disturb_the_run()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory);
        supervisor.AddObserver(new ThrowingObserver());

        var results = await supervisor.Run();

        results.ExitCode.ShouldBe(0);
        results.CleanPasses.Count.ShouldBe(1);
    }

    private sealed class ThrowingObserver : ISupervisorObserver
    {
        public void TestUpdated(WorkerLaunchContext worker, WorkerTestUpdate update)
            => throw new InvalidOperationException("a dashboard that cannot fail a run");
    }

    [Fact]
    public async Task a_real_mtp_host_reports_a_test_in_progress_before_its_verdict()
    {
        var workerPath = SampleWorker.Path;
        File.Exists(workerPath).ShouldBeTrue($"The sample worker was not built at {workerPath}");

        await using var worker = await MtpWorkerClient.Launch(workerPath);

        var updates = new List<WorkerTestUpdate>();
        worker.OnTestUpdate(update => { lock (updates) updates.Add(update); });

        var result = await worker.Run(["Basics/passes"]);
        result.Outcomes.ShouldHaveSingleItem().State.ShouldBe(WorkerTestState.Passed);

        WorkerTestUpdate[] seen;
        lock (updates) seen = updates.ToArray();

        // The in-progress node Bobcat's MTP host publishes at ScenarioStarted, then the verdict.
        seen.Select(u => u.Uid).Distinct().ShouldBe(["Basics/passes"]);
        seen.First().InProgress.ShouldBeTrue();
        seen.First().State.ShouldBeNull();
        seen.Last().InProgress.ShouldBeFalse();
        seen.Last().State.ShouldBe(WorkerTestState.Passed);
    }

    private static class SampleWorker
    {
        public static readonly string Path = locate();

        private static string locate()
        {
            var configuration = System.IO.Path.GetFileName(
                System.IO.Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar))!);

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && directory.Name != "src") directory = directory.Parent;

            if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

            return System.IO.Path.Combine(
                directory.FullName, "Bobcat.Supervisor.SampleWorker", "bin", configuration, "net10.0",
                OperatingSystem.IsWindows()
                    ? "Bobcat.Supervisor.SampleWorker.exe"
                    : "Bobcat.Supervisor.SampleWorker");
        }
    }
}
