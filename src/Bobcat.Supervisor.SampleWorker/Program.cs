using Bobcat.CodeFirst;
using Bobcat.Engine;
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
    private static int _executedInThisProcess;

    public static Task<int> Main(string[] args)
        => BobcatTestApplication.Run(args, runner =>
        {
            runner.AddSpecification<Basics>();
            // Registered last so that, when batched, it runs after the others — which is what
            // makes "did anything else run in my process?" a reliable signal.
            runner.AddSpecification<Fussy>();

            // Inert unless armed. When it is, no scenario in this process can run, and the
            // supervisor must hear that as a reported failure rather than a crash (issue #123).
            runner.Suite.AddResource(new BrokerThatWillNotStart());
        });

    /// <summary>
    /// Throws from <see cref="Start"/> when <c>BOBCAT_START_FAILS</c> is set — the broker that
    /// is down this morning.
    /// </summary>
    private sealed class BrokerThatWillNotStart : ITestResource
    {
        public string Name => "broker";

        public Task Start()
        {
            if (Environment.GetEnvironmentVariable("BOBCAT_START_FAILS") == "true")
            {
                throw new InvalidOperationException("the broker refused the connection");
            }

            return Task.CompletedTask;
        }

        public Task ResetBetweenScenarios() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }


    /// <summary>
    /// Every scenario here is one probe step. It is declared through the raw <c>Step</c> escape
    /// hatch rather than <c>Then</c> on purpose: these probes crash, exit and throw to exercise the
    /// supervisor, and an exception escaping one must reach the platform as an <i>error</i>, not be
    /// folded into an assertion failure the way a <c>Then</c> body's would.
    /// </summary>
    public abstract class ProbeSpecification : Specification
    {
        protected void Probe(string text, Action body)
            => Step(StepKind.Then, text, (_, _, _) =>
            {
                Interlocked.Increment(ref _executedInThisProcess);
                body();
                return Task.CompletedTask;
            });
    }

    public class Basics : ProbeSpecification
    {
        [Scenario] public void passes() => Probe("it passes", () => { });
        [Scenario] public void also_passes() => Probe("it also passes", () => { });
        [Scenario] public void always_fails() => Probe("it never works", () => throw new InvalidOperationException("this one never works"));

        /// <summary>
        /// Wedges the process the way a real hung test does (issues #145/#147): a synchronous
        /// wait that never completes, so an exit request cannot finish the run and the process
        /// only dies when something outside kills it. Instant and green when unarmed.
        /// </summary>
        [Scenario]
        public void hangs_when_armed() => Probe("the worker hangs if BOBCAT_HANG is set", () =>
        {
            if (Environment.GetEnvironmentVariable("BOBCAT_HANG") == "true")
            {
                Thread.Sleep(Timeout.Infinite);
            }
        });
    }

    public class Fussy : ProbeSpecification
    {
        /// <summary>
        /// Only passes when nothing else ran in this process — the Marten/Wolverine "only works if
        /// it is the only test in the process" case, made observable.
        /// </summary>
        [Scenario(Tags = ["isolated", "retry(2)"])]
        public void only_works_alone() => Probe("nothing else has run in this process", () =>
        {
            if (_executedInThisProcess > 1)
            {
                throw new InvalidOperationException(
                    $"not alone: {_executedInThisProcess - 1} other scenario(s) ran in this process first");
            }
        });

        /// <summary>
        /// Fails on its first execution and passes afterwards. The counter lives in a file so it
        /// survives the process being thrown away, which is the whole point when the retry happens
        /// somewhere else.
        /// </summary>
        [Scenario(Tags = ["retry(3)"])]
        public void flaky_until_second_attempt() => Probe("this is at least the second attempt", () =>
        {
            var path = Environment.GetEnvironmentVariable("BOBCAT_FLAKY_STATE");
            if (path is null) return; // not armed — behave

            var attempts = File.Exists(path) && int.TryParse(File.ReadAllText(path), out var n) ? n : 0;
            attempts++;
            File.WriteAllText(path, attempts.ToString());

            if (attempts < 2) throw new InvalidOperationException($"flaky: attempt {attempts}");
        });

        /// <summary>Kills the worker outright, so the supervisor's crash handling is exercised for real.</summary>
        [Scenario]
        public void kills_the_worker_when_armed() => Probe("the worker is killed if BOBCAT_CRASH is set", () =>
        {
            if (Environment.GetEnvironmentVariable("BOBCAT_CRASH") == "true") Environment.Exit(70);
        });

        /// <summary>
        /// Dies the way a real worker usually does: an unhandled exception on a foreground thread,
        /// which terminates the process after the CLR prints a stack trace to stderr. Join never
        /// returns, so this is deterministic rather than timing-dependent.
        /// </summary>
        [Scenario]
        public void dies_with_an_unhandled_exception_when_armed() => Probe("the worker falls over if BOBCAT_UNHANDLED is set", () =>
        {
            if (Environment.GetEnvironmentVariable("BOBCAT_UNHANDLED") != "true") return;

            var thread = new Thread(() => throw new InvalidOperationException("the worker fell over"));
            thread.Start();
            thread.Join();
        });
    }
}
