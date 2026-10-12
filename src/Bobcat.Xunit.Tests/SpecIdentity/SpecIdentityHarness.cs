using System.Diagnostics;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.SpecIdentityChecks;

/// <summary>
/// Issue #391's rules for "run this specification", asserted against a real host: list a suite's
/// identities without running anything, translate an identity into the framework's own filter, and
/// run exactly what was named.
/// </summary>
/// <remarks>
/// These rules used to pin two lanes from one place: this file was also linked into
/// <c>Bobcat.Mtp.Tests</c>, which drove the Gherkin host through the same assertions. That project
/// went with the Gherkin lane (bobcat#471), and the harness stays separate from its one remaining
/// user so a second host can be added without touching the rules.
/// </remarks>
internal sealed class SpecIdentityHarness : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "bobcat-identity-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>The executable a project in <c>src/</c> builds, in this run's configuration.</summary>
    public static string HostIn(string project)
    {
        var configuration = Path.GetFileName(Path.GetDirectoryName(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

        return Path.Combine(
            directory.FullName, project, "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? project + ".exe" : project);
    }

    /// <summary>Launches <paramref name="host"/> as a real process and returns its exit code and output.</summary>
    public static async Task<(int ExitCode, string Output)> Run(
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

        // The host decides its own report. A test process that silenced its own (Bobcat.Xunit.Tests
        // does, in a module initializer) would otherwise silence the host's through inheritance.
        info.Environment.Remove("BOBCAT_SPEC_CONSOLE");

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token);

        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>Asks a host to list what it specifies, without running any of it.</summary>
    public async Task<SpecManifest> List(string host)
    {
        var path = Path.Combine(_directory, Path.GetFileName(host) + ".specs.json");

        var (exitCode, output) = await Run(
            host,
            new Dictionary<string, string> { [SpecManifest.PathVariable] = path },
            "--list-tests");

        exitCode.ShouldBe(0, output);
        File.Exists(path).ShouldBeTrue($"No manifest was written.\n{output}");

        return SpecManifest.Read(path);
    }

    /// <summary>
    /// A plain listing — nobody asked for a manifest — writes nothing. A suite that wrote one
    /// anyway would litter every <c>--list-tests</c> an IDE makes.
    /// </summary>
    public async Task NothingIsWrittenWhenNothingAsked(string host)
    {
        var (exitCode, output) = await Run(host, null, "--list-tests");

        exitCode.ShouldBe(0, output);
        Directory.Exists(_directory).ShouldBeFalse();
    }

    /// <summary>
    /// Names <paramref name="identities"/> exactly as a monitor would, translates them through the
    /// suite's own manifest, and asserts the host ran that many specifications and no more.
    /// </summary>
    /// <param name="expectedArguments">
    /// The lane's own filter spelling, when the test pins it; null to assert only the run.
    /// </param>
    /// <param name="requireGreen">
    /// False when a named specification fails on purpose: the claim is then only what ran.
    /// </param>
    public async Task RunsExactly(
        string host, string[] identities, string[]? expectedArguments = null, bool requireGreen = true)
    {
        var manifest = await List(host);
        var arguments = SpecFilterArguments.For(manifest, SpecSelection.Of(identities));

        if (expectedArguments is not null) arguments.ShouldBe(expectedArguments);

        var (exitCode, output) = await Run(host, null, [.. arguments]);

        if (requireGreen) exitCode.ShouldBe(0, output);
        output.ShouldContain($"total: {identities.Length}");
    }

    /// <summary>
    /// The rejection a resident runner owes a monitor. It matters that this is an exception and not
    /// a run: a framework asked for a test it does not have runs nothing and exits zero, which reads
    /// as a green run of the specification someone wanted.
    /// </summary>
    public async Task RefusesAnIdentityItDoesNotHave(string host, string identity)
    {
        var manifest = await List(host);

        Should.Throw<ArgumentException>(() => SpecFilterArguments.For(manifest, SpecSelection.Of(identity)))
            .Message.ShouldContain(identity);
    }
}
