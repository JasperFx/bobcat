using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Where the end-to-end suites find the real worker processes they drive, and how they name the
/// tests inside them.
/// </summary>
internal static class WorkerHosts
{
    /// <summary>
    /// <c>Bobcat.Supervisor.ProjectedWorker</c>: an ordinary projected xUnit v3 suite whose
    /// specifications pass, fail, flake, hang, crash and ask to be isolated on purpose. The worker
    /// every end-to-end supervisor test drives.
    /// </summary>
    public static string ProjectedWorker { get; } = Locate("Bobcat.Supervisor.ProjectedWorker");

    /// <summary>
    /// <c>Bobcat.Supervisor.SampleWorker</c>: the Bobcat MTP (Gherkin lane) host the projected
    /// worker replaced. Only for the assertions nothing but that host can produce.
    /// </summary>
    // Phase 3: delete with SampleWorker
    public static string SampleWorker { get; } = Locate("Bobcat.Supervisor.SampleWorker");

    /// <summary>The executable a project in <c>src/</c> builds, in this run's configuration.</summary>
    public static string Locate(string project)
    {
        var configuration = Path.GetFileName(
            Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

        return Path.Combine(
            directory.FullName, project, "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? project + ".exe" : project);
    }

    /// <summary>
    /// The projected worker's tests by <c>Class.method</c>. xUnit's uids are opaque hashes, so a
    /// test is found by its display name — xUnit's fully qualified method name — and then run by
    /// the uid it was discovered with, which is the realistic flow against any foreign framework.
    /// </summary>
    public static bool IsNamed(string displayName, string classAndMethod)
        => displayName.EndsWith("." + classAndMethod, StringComparison.Ordinal);

    /// <summary>Discovers on <paramref name="worker"/> and returns the uid of each named test, in order.</summary>
    public static async Task<string[]> UidsOf(MtpWorkerClient worker, params string[] classAndMethods)
    {
        var tests = await worker.Discover();
        return [.. classAndMethods.Select(name => tests.Single(t => IsNamed(t.DisplayName, name)).Uid)];
    }
}
