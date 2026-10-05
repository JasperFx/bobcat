using System.Diagnostics;
using System.Text.RegularExpressions;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Mtp.Tests;

/// <summary>
/// Issue #391: "run this specification" means the same thing in both lanes. One identity per lane,
/// listed and then run, against the real hosts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both lanes in one class, deliberately.</b> The issue's own words: pinning them with the same
/// small corpus is what keeps them from drifting. Split across two test projects, the Gherkin half
/// could gain a rule the projected half never heard about and nothing would say so — which is the
/// same reasoning as <c>MarkerSpecNamingAgreementTests</c> and <c>SliceTagParsingAgreementTests</c>.
/// </para>
/// <para>
/// It lives in <c>Bobcat.Mtp.Tests</c> because that is where Bobcat already launches a test host as
/// a real process, and both lanes genuinely are MTP hosts — that is the one thing they have in
/// common, and the reason an identity can be a lane-neutral request at all.
/// </para>
/// </remarks>
public class SpecIdentityEndToEndTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "bobcat-identity-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static string hostIn(string project, string executable)
    {
        var configuration = Path.GetFileName(Path.GetDirectoryName(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

        return Path.Combine(
            directory.FullName, project, "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? executable + ".exe" : executable);
    }

    private static readonly string gherkinHost =
        hostIn("Bobcat.Mtp.GeneratedHost", "Bobcat.Mtp.GeneratedHost");

    private static readonly string projectedHost =
        hostIn("Bobcat.Xunit.Samples", "Bobcat.Xunit.Samples");

    private static async Task<(int ExitCode, string Output)> run(
        string host, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        File.Exists(host).ShouldBeTrue($"The host was not built at {host}");

        var info = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(host)!
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        // Nothing on the wire: a suite must not reach for a console to answer a listing request,
        // and the projected lane's own spec report is quiet with no terminal attached anyway.
        info.Environment["BOBCAT_MONITOR"] = "0";

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token);

        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>Asks a host to list what it specifies, without running any of it.</summary>
    private async Task<SpecManifest> list(string host)
    {
        var path = Path.Combine(_directory, Path.GetFileName(host) + ".specs.json");

        var (exitCode, output) = await run(
            host,
            new Dictionary<string, string> { [SpecManifest.PathVariable] = path },
            "--list-tests");

        exitCode.ShouldBe(0, output);
        File.Exists(path).ShouldBeTrue($"No manifest was written.\n{output}");

        return SpecManifest.Read(path);
    }

    // --- Listing, in both lanes.

    [Fact]
    public async Task a_gherkin_suite_lists_its_identities_without_running_anything()
    {
        var manifest = await list(gherkinHost);

        manifest.Lane.ShouldBe(SpecManifest.GherkinLane);
        manifest.Framework.ShouldBe(SpecManifest.BobcatFramework);
        manifest.Suite.ShouldBe("Bobcat.Mtp.GeneratedHost");

        manifest.Identities.ShouldBe([
            "Ordering/An order can be emptied",
            "Ordering/An order is accepted",
            "Shipping/A shipment is labelled"
        ]);

        // The identity IS the platform uid here, so there is nothing for a filter to translate.
        manifest.Specs.ShouldAllBe(spec => spec.QualifiedTestMethod == null);
    }

    [Fact]
    public async Task a_projected_suite_lists_its_identities_and_the_method_each_one_is()
    {
        var manifest = await list(projectedHost);

        manifest.Lane.ShouldBe(SpecManifest.ProjectedLane);
        manifest.Framework.ShouldBe(SpecManifest.XunitFramework);
        manifest.Suite.ShouldBe("Bobcat.Xunit.Samples");

        manifest.For("Calculator/using sentences").ShouldNotBeNull()
            .QualifiedTestMethod
            .ShouldBe("Bobcat.Xunit.Samples.Specs.CalculatorSpecs.using_sentences");
    }

    [Fact]
    public async Task a_projected_listing_covers_every_specification_however_its_steps_are_declared()
    {
        // The gap that made the first cut of this wrong. A projected spec declares its steps by
        // marker comments, by [BobcatStep] interceptors, or by grammar calls, and only the first
        // reaches DeclaredSteps.Register — so a listing keyed on that set named 8 of this suite's
        // 41 specifications while the other 33 rendered and published verdicts perfectly well.
        // What makes a test a specification is that [BobcatScenario] records it.
        var manifest = await list(projectedHost);
        var (_, output) = await run(projectedHost, null, "--list-tests");

        // The count comes from the platform rather than from a literal, because the claim is that
        // the listing covers EVERY specification — so the right number is whatever the suite has,
        // and the test should hold as the sample corpus grows. Two literals had to be edited the
        // first time it did (adding the VerifyObject samples took it to 45), and that edit is
        // indistinguishable from someone quietly relaxing the assertion to match a regression.
        var found = int.Parse(Regex.Match(output, @"found (\d+) test\(s\)").Groups[1].Value);

        found.ShouldBeGreaterThan(
            8,
            "the suite should have far more specifications than the 8 that declare marker steps, "
            + "or this test cannot tell the two rules apart");

        manifest.Specs.Count.ShouldBe(
            found,
            "every test [BobcatScenario] records is a specification, however its steps are declared");
    }

    [Fact]
    public async Task nothing_is_written_when_nothing_asked()
    {
        var (exitCode, _) = await run(gherkinHost, null, "--list-tests");

        exitCode.ShouldBe(0);
        Directory.Exists(_directory).ShouldBeFalse();
    }

    // --- Running by identity, in both lanes.

    [Fact]
    public async Task a_gherkin_suite_runs_one_specification_named_by_identity()
    {
        var manifest = await list(gherkinHost);
        var selection = SpecSelection.Of("Ordering/An order is accepted");

        var arguments = SpecFilterArguments.For(manifest, selection);
        arguments.ShouldBe(["--filter-uid", "Ordering/An order is accepted"]);

        var (exitCode, output) = await run(gherkinHost, null, [.. arguments]);

        exitCode.ShouldBe(0, output);
        output.ShouldContain("total: 1");
    }

    [Fact]
    public async Task a_projected_suite_runs_one_specification_named_by_the_same_kind_of_identity()
    {
        // The monitor sent an identity, exactly as it would to the Gherkin host. The translation
        // to this framework's own method filter happened here, from the suite's own manifest.
        var manifest = await list(projectedHost);
        var selection = SpecSelection.Of("Calculator/using sentences");

        var arguments = SpecFilterArguments.For(manifest, selection);
        arguments.ShouldBe([
            "--filter-method", "Bobcat.Xunit.Samples.Specs.CalculatorSpecs.using_sentences"
        ]);

        var (exitCode, output) = await run(projectedHost, null, [.. arguments]);

        exitCode.ShouldBe(0, output);
        output.ShouldContain("total: 1");
    }

    [Fact]
    public async Task several_identities_run_together_in_either_lane()
    {
        var gherkin = await list(gherkinHost);

        var (exitCode, output) = await run(gherkinHost, null,
            [.. SpecFilterArguments.For(
                gherkin,
                SpecSelection.Of("Ordering/An order is accepted", "Shipping/A shipment is labelled"))]);

        exitCode.ShouldBe(0, output);
        output.ShouldContain("total: 2");

        var projected = await list(projectedHost);

        (exitCode, output) = await run(projectedHost, null,
            [.. SpecFilterArguments.For(
                projected,
                SpecSelection.Of("Calculator/using sentences", "Calculator/asserting values"))]);

        output.ShouldContain("total: 2");
    }

    [Fact]
    public async Task an_identity_the_suite_does_not_have_is_refused_before_anything_is_launched()
    {
        // The rejection a resident runner owes a monitor. It matters that this is an exception and
        // not a run: a framework asked for a test it does not have runs nothing and exits zero,
        // which reads as a green run of the specification someone wanted.
        var manifest = await list(projectedHost);

        Should.Throw<ArgumentException>(() => SpecFilterArguments.For(
                manifest, SpecSelection.Of("Calculator/no such specification")))
            .Message.ShouldContain("Calculator/no such specification");
    }
}
