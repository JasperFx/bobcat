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
        var (_, output) = await SpecIdentityHarness.Run(
            host, new Dictionary<string, string> { ["BOBCAT_SPEC_CONSOLE"] = "1" },
            "--filter-class", acceptanceClass);

        // Spectre wraps the report at whatever width it detects for a redirected stream, and that
        // differs between machines (CI split the passing spec's heading where a laptop did not), so
        // the text is compared with every run of whitespace, line breaks included, read as one space.
        var text = System.Text.RegularExpressions.Regex.Replace(output, @"\s+", " ");

        text.ShouldContain("ACCEPTANCE: FAILING");
        text.ShouldContain("This line is always false");
        text.ShouldContain("OK (acceptance");
        text.ShouldContain(SpecLifecycle.ReadyToPromote);
        text.ShouldNotContain(" FAILED", Case.Sensitive);
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
