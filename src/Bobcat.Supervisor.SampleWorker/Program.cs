using Bobcat;
using Bobcat.Mtp;
using Bobcat.Runtime;

namespace Bobcat.Supervisor.SampleWorker;

/// <summary>
/// A Bobcat MTP host whose scenarios can tell whether the supervisor actually did what it
/// claimed — ran them alone, or gave them another process. Without that, an isolation test only
/// proves the supervisor launched something.
/// </summary>
public static class Program
{
    /// <summary>Scenarios that have executed in THIS process. The isolation detector.</summary>
    internal static int ExecutedInThisProcess;

    public static Task<int> Main(string[] args)
        => BobcatTestApplication.Run(args, runner =>
        {
            // Registered explicitly rather than scanned, and Fussy LAST on purpose: when the
            // supervisor batches these into one process, "did anything else run in my process?"
            // is only a reliable signal if the scenario asking it runs after the others.
            // ScanForFeatures would leave that to assembly metadata order.
            runner.AddFeature(Basics_Feature.Define());
            runner.AddFeature(Fussy_Feature.Define());

            // Inert unless armed. When it is, no scenario in this process can run, and the
            // supervisor must hear that as a reported failure rather than a crash (issue #123).
            runner.Resources.Add(new BrokerThatWillNotStart());
        });

    /// <summary>
    /// Throws from <see cref="ITestResource.StartAsync"/> when <c>BOBCAT_START_FAILS</c> is set —
    /// the broker that is down this morning.
    /// </summary>
    private sealed class BrokerThatWillNotStart : ITestResource
    {
        public string Name => "broker";

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (Environment.GetEnvironmentVariable("BOBCAT_START_FAILS") == "true")
            {
                throw new InvalidOperationException("the broker refused the connection");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetBetweenScenarios() => Task.CompletedTask;
    }
}

/// <summary>
/// Every scenario here is one probe step, and every probe counts itself so "did anything else run
/// in my process?" stays answerable.
/// </summary>
/// <remarks>
/// These probes crash, exit and throw on purpose. An exception escaping one must reach the
/// platform as an <i>error</i> rather than an assertion failure, which is what a step body does:
/// <c>Executor.executeStep</c> marks anything that escapes as errored.
/// </remarks>
public abstract class ProbeFixture : Fixture
{
    protected static void Probe(Action body)
    {
        Interlocked.Increment(ref Program.ExecutedInThisProcess);
        body();
    }
}

[FixtureTitle("Basics")]
public class BasicsFixture : ProbeFixture
{
    [Then("it passes")]
    public void Passes() => Probe(() => { });

    [Then("it also passes")]
    public void AlsoPasses() => Probe(() => { });

    [Then("it never works")]
    public void NeverWorks() => Probe(() => throw new InvalidOperationException("this one never works"));

    /// <summary>
    /// Wedges the process the way a real hung test does (issues #145/#147): a synchronous wait
    /// that never completes, so an exit request cannot finish the run and the process only dies
    /// when something outside kills it. Instant and green when unarmed.
    /// </summary>
    [Then("the worker hangs if BOBCAT_HANG is set")]
    public void HangsWhenArmed() => Probe(() =>
    {
        if (Environment.GetEnvironmentVariable("BOBCAT_HANG") == "true")
        {
            Thread.Sleep(Timeout.Infinite);
        }
    });
}

[FixtureTitle("Fussy")]
public class FussyFixture : ProbeFixture
{
    /// <summary>
    /// Only passes when nothing else ran in this process — the Marten/Wolverine "only works if it
    /// is the only test in the process" case, made observable.
    /// </summary>
    [Then("nothing else has run in this process")]
    public void OnlyWorksAlone() => Probe(() =>
    {
        if (Program.ExecutedInThisProcess > 1)
        {
            throw new InvalidOperationException(
                $"not alone: {Program.ExecutedInThisProcess - 1} other scenario(s) ran in this process first");
        }
    });

    /// <summary>
    /// Fails on its first execution and passes afterwards. The counter lives in a file so it
    /// survives the process being thrown away, which is the whole point when the retry happens
    /// somewhere else.
    /// </summary>
    [Then("this is at least the second attempt")]
    public void FlakyUntilSecondAttempt() => Probe(() =>
    {
        var path = Environment.GetEnvironmentVariable("BOBCAT_FLAKY_STATE");
        if (path is null) return; // not armed — behave

        var attempts = File.Exists(path) && int.TryParse(File.ReadAllText(path), out var n) ? n : 0;
        attempts++;
        File.WriteAllText(path, attempts.ToString());

        if (attempts < 2) throw new InvalidOperationException($"flaky: attempt {attempts}");
    });

    /// <summary>Kills the worker outright, so the supervisor's crash handling is exercised for real.</summary>
    [Then("the worker is killed if BOBCAT_CRASH is set")]
    public void KillsTheWorkerWhenArmed() => Probe(() =>
    {
        if (Environment.GetEnvironmentVariable("BOBCAT_CRASH") == "true") Environment.Exit(70);
    });

    /// <summary>
    /// Dies the way a real worker usually does: an unhandled exception on a foreground thread,
    /// which terminates the process after the CLR prints a stack trace to stderr. Join never
    /// returns, so this is deterministic rather than timing-dependent.
    /// </summary>
    [Then("the worker falls over if BOBCAT_UNHANDLED is set")]
    public void DiesWithAnUnhandledExceptionWhenArmed() => Probe(() =>
    {
        if (Environment.GetEnvironmentVariable("BOBCAT_UNHANDLED") != "true") return;

        var thread = new Thread(() => throw new InvalidOperationException("the worker fell over"));
        thread.Start();
        thread.Join();
    });
}
