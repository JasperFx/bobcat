using Microsoft.Extensions.Configuration;

namespace Bobcat.Supervisor.ProjectedWorker;

/// <summary>
/// Counts every probe that executes in this process, so a probe can tell whether the supervisor
/// actually did what it claimed — ran it alone, or gave it another process. Without that, an
/// isolation test only proves the supervisor launched something.
/// </summary>
/// <remarks>
/// These probes crash, exit and throw on purpose. Every one of them is inert — instant and green —
/// unless its environment variable arms it, so a plain run of this worker sees only the one
/// specification that is meant to be red.
/// </remarks>
internal static class Probe
{
    private static int _executedInThisProcess;

    /// <summary>Probes that have executed in THIS process. The isolation detector.</summary>
    public static int ExecutedInThisProcess => Volatile.Read(ref _executedInThisProcess);

    public static void Run(Action body)
    {
        Interlocked.Increment(ref _executedInThisProcess);
        body();
    }

    public static bool IsArmed(string variable) => Environment.GetEnvironmentVariable(variable) == "true";
}

/// <summary>
/// The specification identities here are load-bearing: Bobcat.Supervisor.Tests finds
/// <c>Basics.passes</c>, <c>Basics.always_fails</c> and friends by name. Rename one only with
/// those tests in hand.
/// </summary>
[BobcatFeature("Basics"), BobcatScenario]
public class Basics
{
    [Fact]
    public void passes()
    {
        // Then it passes
        // (Slow only when BOBCAT_SLOW is set: MTP 2.x's server coalesces node updates inside a
        // ~100 ms batch, so a test faster than that reports only its verdict, never in-progress.)
        Probe.Run(() =>
        {
            if (Probe.IsArmed("BOBCAT_SLOW")) Thread.Sleep(500);
        });

        // And the worker writes its connection strings if BOBCAT_CONNECTION_PROBE is set
        writeConnectionStrings();
    }

    /// <summary>
    /// Reads the worker's connection strings exactly as an application would, through
    /// <c>IConfiguration.GetConnectionString</c> (issue #414), and writes them to the file
    /// <c>BOBCAT_CONNECTION_PROBE</c> names. Not a probe of its own: it must not count as a second
    /// test having run.
    /// </summary>
    private static void writeConnectionStrings()
    {
        var path = Environment.GetEnvironmentVariable("BOBCAT_CONNECTION_PROBE");
        if (path is null) return;

        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var section = configuration.GetSection("ConnectionStrings");
        File.WriteAllLines(path, section.GetChildren()
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key}={configuration.GetConnectionString(x.Key)}"));
    }

    [Fact]
    public void also_passes()
    {
        // Then it also passes
        Probe.Run(() => { });
    }

    [Fact]
    public void always_fails()
    {
        // Then it never works
        Probe.Run(() => throw new InvalidOperationException("this one never works"));
    }

    /// <summary>
    /// Wedges the process the way a real hung test does (issues #145/#147): a synchronous wait
    /// that never completes, so an exit request cannot finish the run and the process only dies
    /// when something outside kills it.
    /// </summary>
    [Fact]
    public void hangs_when_armed()
    {
        // Then the worker hangs if BOBCAT_HANG is set
        Probe.Run(() =>
        {
            if (Probe.IsArmed("BOBCAT_HANG")) Thread.Sleep(Timeout.Infinite);
        });
    }
}

/// <summary>
/// The probes that need the supervisor's help. Declaration order matters: the
/// <see cref="InDeclarationOrder"/> orderer runs <see cref="only_works_alone"/> first in this class,
/// which a per-class isolation sweep depends on.
/// </summary>
/// <remarks>
/// The traits are plain xUnit <see cref="TraitAttribute"/>s with the keys in
/// <c>Bobcat.Resilience.ResilienceTags</c>: the supervisor reads traits off the wire at discovery,
/// whatever framework wrote them.
/// </remarks>
[BobcatFeature("Fussy"), BobcatScenario]
public class Fussy
{
    /// <summary>
    /// Only passes when nothing else ran in this process — the Marten/Wolverine "only works if it
    /// is the only test in the process" case, made observable.
    /// </summary>
    [Fact]
    [Trait("Isolated", "true")]
    [Trait("Retry", "2")]
    public void only_works_alone()
    {
        // Then nothing else has run in this process
        Probe.Run(() =>
        {
            if (Probe.ExecutedInThisProcess > 1)
            {
                throw new InvalidOperationException(
                    $"not alone: {Probe.ExecutedInThisProcess - 1} other test(s) ran in this process first");
            }
        });
    }

    /// <summary>
    /// Fails on its first execution and passes afterwards. The counter lives in a file so it
    /// survives the process being thrown away, which is the whole point when the retry happens
    /// somewhere else.
    /// </summary>
    [Fact]
    [Trait("Retry", "3")]
    public void flaky_until_second_attempt()
    {
        // Then this is at least the second attempt
        Probe.Run(() =>
        {
            var path = Environment.GetEnvironmentVariable("BOBCAT_FLAKY_STATE");
            if (path is null) return; // not armed — behave

            var attempts = File.Exists(path) && int.TryParse(File.ReadAllText(path), out var n) ? n : 0;
            attempts++;
            File.WriteAllText(path, attempts.ToString());

            if (attempts < 2) throw new InvalidOperationException($"flaky: attempt {attempts}");
        });
    }

    /// <summary>Kills the worker outright, so the supervisor's crash handling is exercised for real.</summary>
    [Fact]
    public void kills_the_worker_when_armed()
    {
        // Then the worker is killed if BOBCAT_CRASH is set
        Probe.Run(() =>
        {
            if (Probe.IsArmed("BOBCAT_CRASH")) Environment.Exit(70);
        });
    }

    /// <summary>
    /// Dies the way a real worker usually does: an unhandled exception on a foreground thread,
    /// which terminates the process after the CLR prints a stack trace to stderr. Join never
    /// returns, so this is deterministic rather than timing-dependent.
    /// </summary>
    [Fact]
    public void dies_with_an_unhandled_exception_when_armed()
    {
        // Then the worker falls over if BOBCAT_UNHANDLED is set
        Probe.Run(() =>
        {
            if (!Probe.IsArmed("BOBCAT_UNHANDLED")) return;

            var thread = new Thread(() => throw new InvalidOperationException("the worker fell over"));
            thread.Start();
            thread.Join();
        });
    }

    /// <summary>
    /// The environment is gone (issue #538): throws <see cref="Bobcat.Engine.SpecCatastrophicException"/>
    /// when <c>BOBCAT_CATASTROPHIC</c> is set. It carries a <c>Retry</c> trait on purpose — a
    /// catastrophic failure must abort the supervised run, never be retried, whatever the trait says.
    /// </summary>
    [Fact]
    [Trait("Retry", "2")]
    public void loses_the_environment_when_armed()
    {
        // Then the environment is lost if BOBCAT_CATASTROPHIC is set
        Probe.Run(() =>
        {
            if (Probe.IsArmed("BOBCAT_CATASTROPHIC"))
            {
                throw new Bobcat.Engine.SpecCatastrophicException("the application could not start");
            }
        });
    }
}
