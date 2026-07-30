using Bobcat.Engine;
using Bobcat.Resilience;
using Bobcat.Runtime;
using Shouldly;
using Spectre.Console;

namespace Bobcat.Tests.Resilience;

/// <summary>
/// What a person actually sees. A hint that suppressed a retry has to say so on the console —
/// otherwise a scenario tagged <c>@retry(3)</c> fails once and its author is left believing the
/// tag stopped working.
/// </summary>
public class RecoveryHintConsoleTests
{
    [NeverRecovers(typeof(NotSupportedException), Because = "this is a real bug")]
    public class DeterministicFixture : Fixture;

    [ClearsOnRetry(typeof(TimeoutException), Because = "the broker is slow to warm up")]
    public class BrokerFixture : Fixture;

    public class PlainFixture : Fixture;

    private static async Task<string> Capture(
        Type fixtureType, string[] tags, Func<int, Exception?> failure)
    {
        var attempts = 0;
        var writer = new StringWriter();
        var original = AnsiConsole.Console;

        // Spectre renders to a static console, so this is the only seam. Restored in a finally so
        // a failing assertion cannot leave the rest of the suite writing into a StringWriter.
        // Safe because xUnit serializes tests within a class and this is the only class in the
        // project that renders — every other runner sets SuppressConsoleOutput. A second
        // rendering test class would need to share a [Collection] with this one.
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No
        });

        try
        {
            var runner = new BobcatRunner { RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3 } };
            runner.AddFeature(new FeatureDefinition("Probe", fixtureType,
            [
                new ScenarioDefinition("boom", tags, (_, plan) =>
                    plan.Add(new DelegateExecutionStep("s1", StepKind.Then, "it explodes",
                        (_, _, _) =>
                        {
                            var thrown = failure(++attempts);
                            return thrown is null ? Task.CompletedTask : Task.FromException(thrown);
                        })))
            ]));

            await runner.RunAll();
        }
        finally
        {
            AnsiConsole.Console = original;
        }

        return writer.ToString();
    }

    [Fact]
    public async Task a_suppressed_retry_explains_itself_on_the_console()
    {
        var output = await Capture(
            typeof(DeterministicFixture), ["retry(3)"], _ => new NotSupportedException("nope"));

        output.ShouldContain("recovery hint applied");
        output.ShouldContain("never recovers");
        output.ShouldContain("this is a real bug");
        output.ShouldContain(nameof(DeterministicFixture));
    }

    [Fact]
    public async Task a_hint_driven_retry_names_the_authors_reason_as_it_retries()
    {
        var output = await Capture(
            typeof(BrokerFixture), [], attempt => attempt == 1 ? new TimeoutException() : null);

        output.ShouldContain("retrying");
        output.ShouldContain("the broker is slow to warm up");
        output.ShouldContain("passed on retry");
    }

    [Fact]
    public async Task an_ordinary_failure_says_nothing_about_hints()
    {
        // The line must be earned. Printing it for every failure would make it invisible.
        var output = await Capture(typeof(PlainFixture), [], _ => new NotSupportedException("nope"));

        output.ShouldNotContain("recovery hint");
    }
}
