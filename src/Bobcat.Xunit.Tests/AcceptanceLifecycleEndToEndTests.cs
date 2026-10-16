using Bobcat.SpecIdentityChecks;
using Shouldly;
using Xunit;

namespace Bobcat.Xunit.Tests;

/// <summary>
/// bobcat#545 against the real <c>Bobcat.Xunit.Samples</c> host: a failing Acceptance spec is reported
/// SKIPPED with its reason whichever way it failed, a passing one passes, and
/// <c>BOBCAT_LIFECYCLE=strict</c> turns the failures — and a <c>Pending = true</c> spec — into real ones.
/// </summary>
/// <remarks>
/// A real process because the mechanism is xUnit's own: a spec whose body threw is only reported
/// skipped because <see cref="AcceptanceTestCase"/> rewrites the message on its way to the platform,
/// and nothing short of the platform's own summary proves that it did.
/// </remarks>
public class AcceptanceLifecycleEndToEndTests
{
    private static readonly string host = SpecIdentityHarness.HostIn("Bobcat.Xunit.Samples");

    private const string acceptanceClass = "*AcceptanceSpecs";

    [Fact]
    public async Task failing_acceptance_specs_are_skipped_with_their_first_failure_and_the_run_is_green()
    {
        var (exitCode, output) = await SpecIdentityHarness.Run(
            host, null, "--filter-class", acceptanceClass, "--output", "Detailed");

        exitCode.ShouldBe(0, output);
        output.ShouldContain("failed: 0");
        output.ShouldContain("succeeded: 1");
        output.ShouldContain("skipped: 2");

        // Gathered wrongs only: the after-test hook's skip is the one exception xUnit holds.
        output.ShouldContain("acceptance spec, not yet passing: And This line is always false");

        // The body threw: only the rewritten TestFailed message makes this a skip.
        output.ShouldContain(
            "acceptance spec, not yet passing: System.NotImplementedException: the behaviour this specification describes");
    }

    [Fact]
    public async Task bobcat_shows_the_failure_in_full_under_its_own_verdict()
    {
        // The end-of-run report is written at process exit. Read through the file the bobcat tool
        // uses rather than the child's stdout: on the Linux CI runner the exit-time write to a
        // redirected stdout never reached the parent, so only MTP's echo of the SKIPPED tests' own
        // output arrived and the passing spec's heading was missing. The file is also laid out at a
        // fixed width, so nothing depends on where a console would have wrapped.
        var report = Path.Combine(Path.GetTempPath(), $"bobcat-acceptance-{Guid.NewGuid():N}.txt");
        try
        {
            await SpecIdentityHarness.Run(
                host,
                new Dictionary<string, string>
                {
                    ["BOBCAT_SPEC_CONSOLE"] = "1",
                    [ProjectedSpecConsole.OutputFileEnvironmentVariable] = report,
                    [ProjectedSpecConsole.WidthEnvironmentVariable] = "240"
                },
                "--filter-class", acceptanceClass);

            File.Exists(report).ShouldBeTrue("the child wrote no spec report");

            // The file is written with colour for the tool to replay; the claims are about the words.
            var text = System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(report), @"\x1b\[[0-9;]*m", "");

            text.ShouldContain("ACCEPTANCE: FAILING");
            text.ShouldContain("This line is always false");
            text.ShouldContain("OK (acceptance — " + SpecLifecycle.ReadyToPromote + ")");
            text.ShouldNotContain(" FAILED", Case.Sensitive);
        }
        finally
        {
            File.Delete(report);
        }
    }

    [Fact]
    public async Task strict_mode_fails_acceptance_specs_and_pending_ones()
    {
        var (exitCode, output) = await SpecIdentityHarness.Run(
            host, new Dictionary<string, string> { [SpecLifecycle.EnvironmentVariable] = SpecLifecycle.StrictValue },
            "--filter-class", acceptanceClass, "--filter-class", "*OneAttributeSpecs");

        exitCode.ShouldNotBe(0, output);
        output.ShouldContain("failed: 3");
        output.ShouldContain("skipped: 0");
        output.ShouldNotContain(SpecLifecycle.SkipReasonPrefix);
        output.ShouldContain(
            "pending specification in strict mode: Facts, in one attribute/a pending specification is declared before its behaviour exists");
    }
}
