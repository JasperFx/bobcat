using System.Diagnostics;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #147 — the one moment a wedged worker's state still exists is immediately before the
/// supervisor kills it. The hook offers that moment to the consumer; these tests prove the
/// offer is bounded (nothing the hook does can change what the run does) and honest (a healthy
/// or already-dead worker never reaches it).
/// </summary>
public class BeforeKillHookTests
{
    private static readonly WorkerKillContext anyContext =
        new(ProcessId: 42, Lane: 0, WorkerPurpose.Lane, "test");

    [Fact]
    public async Task a_hook_that_throws_never_stops_the_kill()
    {
        await MtpWorkerClient.InvokeBounded(
            _ => throw new InvalidOperationException("the dump tool is missing"),
            anyContext, TimeSpan.FromSeconds(5));

        await MtpWorkerClient.InvokeBounded(
            async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("the dump tool broke later");
            },
            anyContext, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task a_hook_that_overruns_is_abandoned_at_the_deadline()
    {
        var clock = Stopwatch.StartNew();

        await MtpWorkerClient.InvokeBounded(
            _ => Task.Delay(TimeSpan.FromMinutes(5)),
            anyContext, TimeSpan.FromMilliseconds(100));

        // Well under the hook's own five minutes: the deadline won.
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task a_hook_that_blocks_synchronously_cannot_stall_the_kill_either()
    {
        // The nastiest consumer bug: a hook that never even returns its Task. Task.Run inside
        // InvokeBounded is what keeps the deadline in charge.
        using var blocked = new ManualResetEventSlim(false);
        var clock = Stopwatch.StartNew();

        await MtpWorkerClient.InvokeBounded(
            _ =>
            {
                blocked.Wait();
                return Task.CompletedTask;
            },
            anyContext, TimeSpan.FromMilliseconds(100));

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        blocked.Set(); // release the abandoned thread
    }

    // ---- against the real worker process ----

    private static readonly string workerPath = SampleWorker.Path;

    [Fact]
    public async Task a_healthy_worker_is_disposed_without_the_hook_ever_firing()
    {
        // A worker that exits when asked has nothing to capture, and a hook that fired on every
        // clean shutdown would bury the one invocation that matters.
        var fired = 0;

        await using (var worker = await MtpWorkerClient.Launch(
                         workerPath,
                         onBeforeKill: _ =>
                         {
                             Interlocked.Increment(ref fired);
                             return Task.CompletedTask;
                         }))
        {
            await worker.Run(["Basics/passes"]);
        }

        fired.ShouldBe(0);
    }

    [Fact]
    public async Task a_wedged_worker_reaches_the_hook_alive_before_the_kill_lands()
    {
        // The wolverine#4100 shape: a test hangs, the worker will not exit when asked, and the
        // only useful artifact — the async stacks on its GC heap — exists exactly until the
        // kill. The hook must therefore see a live process, after the exit grace ran out.
        WorkerKillContext? seen = null;
        var aliveInsideHook = false;

        var worker = await MtpWorkerClient.Launch(
            workerPath,
            new Dictionary<string, string> { ["BOBCAT_HANG"] = "true" },
            onBeforeKill: context =>
            {
                seen = context;
                using var process = Process.GetProcessById(context.ProcessId!.Value);
                aliveInsideHook = !process.HasExited;
                return Task.CompletedTask;
            });

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.OnTestUpdate(update =>
        {
            if (update.InProgress) started.TrySetResult();
        });

        var run = worker.Run(["Basics/hangs when armed"]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await worker.DisposeAsync();

        seen.ShouldNotBeNull();
        aliveInsideHook.ShouldBeTrue();
        seen.Reason.ShouldContain("did not exit");
        seen.ProcessId.ShouldNotBeNull();

        // The abandoned run collapses once its worker is gone; observe it so nothing leaks.
        try { await run; } catch { /* either a fault result or a dispose-time exception — both fine */ }
    }

    /// <summary>Same locator the other end-to-end suites use.</summary>
    private static class SampleWorker
    {
        public static string Path { get; } = locate();

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
