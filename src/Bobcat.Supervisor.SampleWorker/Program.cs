using Bobcat;
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
            runner.AddFeature(Basics());
            // Registered last so that, when batched, it runs after the others — which is what
            // makes "did anything else run in my process?" a reliable signal.
            runner.AddFeature(Fussy());
        });

    public class SampleFixture : Fixture;

    private static FeatureDefinition Basics() => new(
        "Basics", typeof(SampleFixture),
        [
            Scenario("passes", [], () => { }),
            Scenario("also passes", [], () => { }),
            Scenario("always fails", [], () => throw new InvalidOperationException("this one never works"))
        ]);

    private static FeatureDefinition Fussy() => new(
        "Fussy", typeof(SampleFixture),
        [
            // Only passes when nothing else ran in this process — the Marten/Wolverine
            // "only works if it is the only test in the process" case, made observable.
            Scenario("only works alone", ["isolated", "retry(2)"], () =>
            {
                if (_executedInThisProcess > 1)
                {
                    throw new InvalidOperationException(
                        $"not alone: {_executedInThisProcess - 1} other scenario(s) ran in this process first");
                }
            }),

            // Fails on its first execution and passes afterwards. The counter lives in a file so
            // it survives the process being thrown away, which is the whole point when the retry
            // happens somewhere else.
            Scenario("flaky until second attempt", ["retry(3)"], () =>
            {
                var path = Environment.GetEnvironmentVariable("BOBCAT_FLAKY_STATE");
                if (path is null) return; // not armed — behave

                var attempts = File.Exists(path) && int.TryParse(File.ReadAllText(path), out var n) ? n : 0;
                attempts++;
                File.WriteAllText(path, attempts.ToString());

                if (attempts < 2) throw new InvalidOperationException($"flaky: attempt {attempts}");
            }),

            // Kills the worker outright, so the supervisor's crash handling is exercised for real.
            Scenario("kills the worker when armed", [], () =>
            {
                if (Environment.GetEnvironmentVariable("BOBCAT_CRASH") == "true") Environment.Exit(70);
            }),

            // Dies the way a real worker usually does: an unhandled exception on a foreground
            // thread, which terminates the process after the CLR prints a stack trace to stderr.
            // Join never returns, so this is deterministic rather than timing-dependent.
            Scenario("dies with an unhandled exception when armed", [], () =>
            {
                if (Environment.GetEnvironmentVariable("BOBCAT_UNHANDLED") != "true") return;

                var thread = new Thread(() => throw new InvalidOperationException("the worker fell over"));
                thread.Start();
                thread.Join();
            })
        ]);

    private static ScenarioDefinition Scenario(string title, string[] tags, Action body)
        => new(title, tags, (_, plan) =>
            plan.Add(new DelegateExecutionStep("step-1", StepKind.Then, title, (_, _, _) =>
            {
                Interlocked.Increment(ref _executedInThisProcess);
                body();
                return Task.CompletedTask;
            })));
}
