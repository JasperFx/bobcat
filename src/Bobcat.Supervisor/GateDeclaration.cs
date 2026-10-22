using Bobcat.Monitoring;

namespace Bobcat.Supervisor;

/// <summary>
/// What a repository's local commit-and-push gate runs (bobcat#729, design of record
/// <c>design/local-push-gates.md</c>): its test projects, the resource <see cref="Tracks"/> each
/// holds while it runs, and one CPU <see cref="WorkerBudget"/> across all of them. <see cref="Run"/>
/// builds once, preflights every track, then runs one <see cref="Supervisor"/> per
/// <see cref="GateUnit"/> — a (project, framework) pair — longest first, never two on one track,
/// never more workers than the budget.
/// </summary>
/// <remarks>
/// <para>
/// Written in C#, beside the targets that already configure each project, because the per-project
/// facts a gate needs — workers, lane isolation (<see cref="MtpWorkerFactory.ConnectionStrings"/>,
/// <see cref="MtpWorkerFactory.EnvironmentFor"/>), the retry policy — are delegates and provider
/// factories that neither MSBuild items nor traits can carry. A project keeps its own
/// <see cref="Supervisor"/> configuration through <see cref="GateProject.Supervise"/>; the gate
/// does not invent a second way to configure one, and adds no retry a project's own policy would
/// not make.
/// </para>
/// <para>
/// A scheduler above supervisors, not one supervisor over many hosts: partitioning, ledger
/// matching, stall handling and <see cref="RunReport"/> are all per host and right as they are.
/// </para>
/// </remarks>
public sealed class GateDeclaration
{
    private readonly List<GateProject> _projects = [];

    /// <summary>
    /// The resources units hold while they run. A unit runs only while it holds every track it
    /// names; tracks are exclusive (capacity 1) and run concurrently with each other.
    /// </summary>
    public List<Track> Tracks { get; } = [];

    /// <summary>
    /// Worker processes running at once, across every unit. Defaults to half the machine's
    /// cores, floor 1 — the clamp each repository's build already applied per supervisor, now one
    /// budget so four suites at "2 workers each" cannot put eight hosts on four cores. A unit that
    /// asks for more than the budget is clamped to it, never held back forever.
    /// </summary>
    public int WorkerBudget { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Which target frameworks each project runs on. Newest only by default.</summary>
    public FrameworkSelection Frameworks { get; set; } = FrameworkSelection.Newest;

    /// <summary>The build configuration the test hosts are found under. <c>Debug</c> by default.</summary>
    public string Configuration { get; set; } = "Debug";

    /// <summary>
    /// What the default build step builds: a solution path, or null to build each declared
    /// project. One <c>dotnet build</c> per framework the gate runs, then every host runs as built.
    /// </summary>
    public string? Solution { get; set; }

    /// <summary>
    /// The caller's promise that every host is already built for every framework the gate will
    /// run, so no build step runs. A missing host is then reported before anything launches.
    /// </summary>
    public bool SkipBuild { get; set; }

    /// <summary>
    /// A build step of the caller's own, in place of the default <c>dotnet build</c> — a Nuke
    /// target's <c>DotNetBuild</c>, say. Called once with the frameworks the gate will run; throw
    /// to fail the gate. Ignored when <see cref="SkipBuild"/> is set.
    /// </summary>
    public Func<GateBuild, CancellationToken, Task>? Build { get; set; }

    /// <summary>
    /// What every unit's <see cref="Supervisor.RunTag"/> starts with: the unit publishes as
    /// <c>{RunTagPrefix}/{project}.{framework}</c> (bobcat#768). Defaults to the process's own
    /// <c>BOBCAT_RUN_TAG</c>, which is how a coordination plan's node becomes one run per unit
    /// under it; null when neither is set, and the units then publish untagged.
    /// </summary>
    public string? RunTagPrefix { get; set; } = Environment.GetEnvironmentVariable(MonitorRunInfo.RunTagVariable) is { Length: > 0 } tag
        ? tag
        : null;

    /// <summary>
    /// Publish every unit's run to the monitor targets <c>BOBCAT_MONITOR_URL</c> names. On by
    /// default — a gate is a real entry point, not a unit test — and each unit is its own run
    /// card, under its own tag. A probe that nobody answers costs one refused connection per unit.
    /// </summary>
    public bool PublishToMonitor { get; set; } = true;

    /// <summary>Where every unit's bracket goes instead of the real monitor. Tests substitute a sink.</summary>
    public IMonitorEventSink? MonitorSink { get; set; }

    /// <summary>Where the plan, progress and the report are written. The console by default.</summary>
    public Action<string> Log { get; set; } = Console.WriteLine;

    /// <summary>
    /// The gate's own record of how long each unit took, which is what orders the next run.
    /// Relative to the working directory; null keeps no record, and every run is then ordered as
    /// a first run. Written after every run, read before it; never committed by the gate.
    /// </summary>
    public string? DurationsPath { get; set; } = Path.Combine(".bobcat", "gate-durations.json");

    /// <summary>
    /// How a unit's worker factory is built when its project does not say
    /// (<see cref="GateProject.WorkerFactory"/>). Default: an <see cref="MtpWorkerFactory"/> over
    /// the built host.
    /// </summary>
    public Func<GateUnit, IWorkerFactory>? WorkerFactory { get; set; }

    /// <summary>
    /// Configuration applied to every unit's supervisor before its project's own
    /// (<see cref="GateProject.Supervise"/>) — stall thresholds, heartbeat, observers, a
    /// repository-wide retry policy. The project's hook runs after and wins.
    /// </summary>
    public Action<GateUnit, Supervisor>? Configure { get; set; }

    /// <summary>The declared projects, in declaration order.</summary>
    public IReadOnlyList<GateProject> Projects => _projects;

    /// <summary>
    /// Declare a test project. <paramref name="path"/> is its <c>.csproj</c>, or the directory
    /// holding one; <paramref name="workers"/> is the lanes its supervisor runs, the same number
    /// the repository's CI target gives it.
    /// </summary>
    public GateProject Project(string path, int workers = 1)
    {
        var project = new GateProject(path, workers);

        if (_projects.Any(p => p.Name.Equals(project.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Project '{project.Name}' is declared twice.", nameof(path));

        _projects.Add(project);
        return project;
    }

    /// <summary>
    /// The units this declaration runs, in declaration order — one per selected framework per
    /// project. Resolving them reads each project file for its frameworks and binds track names,
    /// so a track nobody declared is an error here rather than a unit that waits forever.
    /// </summary>
    public IReadOnlyList<GateUnit> Units()
    {
        var tracks = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in Tracks)
        {
            if (!tracks.TryAdd(track.Name, track))
                throw new InvalidOperationException($"Track '{track.Name}' is declared twice.");
        }

        var units = new List<GateUnit>();
        foreach (var project in _projects)
        {
            var held = project.Tracks.Select(name => tracks.TryGetValue(name, out var track)
                ? track
                : throw new InvalidOperationException(
                    $"Project '{project.Name}' names track '{name}', which the gate does not declare. " +
                    $"Declared: {(tracks.Count == 0 ? "(none)" : string.Join(", ", tracks.Keys))}.")).ToArray();

            var declared = project.DeclaredFrameworks;
            if (declared.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Project '{project.Name}' declares no target framework the gate can read from {project.Path}. " +
                    "Name them with .Targeting(...).");
            }

            foreach (var framework in Frameworks.Select(declared))
            {
                units.Add(new GateUnit(project, framework, held, project.Workers,
                    GateUnit.ExecutableFor(project, Configuration, framework)));
            }
        }

        return units;
    }

    /// <summary>Run the gate. See <see cref="Gate.Run"/>.</summary>
    public Task<GateResults> Run(CancellationToken ct = default) => new Gate(this).Run(ct);
}

/// <summary>
/// One declared test project and what its supervisor needs. Built by
/// <see cref="GateDeclaration.Project"/>; the fluent members return the project so a declaration
/// reads as one line per project.
/// </summary>
public sealed class GateProject
{
    private readonly List<string> _tracks = [];
    private IReadOnlyList<string>? _frameworks;

    internal GateProject(string path, int workers)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A project needs a path.", nameof(path));
        if (workers < 1) throw new ArgumentOutOfRangeException(nameof(workers), "A project runs on at least one worker.");

        var full = System.IO.Path.GetFullPath(path);
        if (System.IO.Directory.Exists(full))
        {
            var candidates = System.IO.Directory.GetFiles(full, "*.csproj");
            full = candidates.Length == 1
                ? candidates[0]
                : System.IO.Path.Combine(full, System.IO.Path.GetFileName(full.TrimEnd(System.IO.Path.DirectorySeparatorChar)) + ".csproj");
        }

        Path = full;
        Name = System.IO.Path.GetFileNameWithoutExtension(full);
        Directory = System.IO.Path.GetDirectoryName(full)!;
        Workers = workers;
    }

    /// <summary>The project file, as a full path.</summary>
    public string Path { get; }

    /// <summary>The project's name: its file name without the extension, which is also the host's.</summary>
    public string Name { get; }

    /// <summary>The directory the host is built under.</summary>
    public string Directory { get; }

    /// <summary>Lanes its supervisor runs, before the gate's budget clamps it.</summary>
    public int Workers { get; set; }

    /// <summary>The tracks this project holds while it runs, by name.</summary>
    public IReadOnlyList<string> Tracks => _tracks;

    /// <summary>
    /// The frameworks the project targets: what <see cref="Targeting"/> declared, else what its
    /// project file says.
    /// </summary>
    public IReadOnlyList<string> DeclaredFrameworks => _frameworks ??= TargetFrameworks.Declared(Path);

    /// <summary>This project's worker factory, when the default <see cref="MtpWorkerFactory"/> over the host will not do.</summary>
    public Func<GateUnit, IWorkerFactory>? WorkerFactory { get; set; }

    /// <summary>This project's supervisor configuration. See <see cref="Supervise"/>.</summary>
    public Action<GateUnit, Supervisor>? Configure { get; set; }

    /// <summary>Hold these tracks while running. A project with none is limited only by the budget.</summary>
    public GateProject On(params string[] tracks)
    {
        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track)) throw new ArgumentException("A track name is empty.", nameof(tracks));
            if (!_tracks.Contains(track, StringComparer.OrdinalIgnoreCase)) _tracks.Add(track);
        }

        return this;
    }

    /// <summary>Declare the frameworks instead of reading them from the project file.</summary>
    public GateProject Targeting(params string[] frameworks)
    {
        if (frameworks.Length == 0) throw new ArgumentException("Name at least one framework.", nameof(frameworks));
        _frameworks = frameworks.Select(f => f.Trim()).ToArray();
        return this;
    }

    /// <summary>
    /// Configure this project's supervisor the way its CI target does — the retry budget and
    /// policy, <see cref="Supervisor.TestFilter"/>, stall and heartbeat settings, known test
    /// durations. Runs after the gate's own defaults and after <see cref="GateDeclaration.Configure"/>,
    /// so it wins; only <see cref="Supervisor.MaxParallelWorkers"/> is the gate's, re-applied
    /// afterwards from the budget.
    /// </summary>
    public GateProject Supervise(Action<GateUnit, Supervisor> configure)
    {
        Configure = configure;
        return this;
    }

    /// <summary>Launch this project's workers through <paramref name="factory"/> — lane connection strings, a kill hook.</summary>
    public GateProject LaunchingWith(Func<GateUnit, IWorkerFactory> factory)
    {
        WorkerFactory = factory;
        return this;
    }

    public override string ToString() => Name;
}

/// <summary>
/// What the gate schedules: one project on one framework, holding its tracks, running on its
/// workers (as declared — the gate clamps to the budget when it starts the unit).
/// </summary>
public sealed record GateUnit(
    GateProject Project,
    string Framework,
    IReadOnlyList<Track> Tracks,
    int Workers,
    string Executable)
{
    /// <summary><c>{project}.{framework}</c> — the unit's name in the plan, the report, and its run tag.</summary>
    public string Name => $"{Project.Name}.{Framework}";

    /// <summary>The run tag this unit publishes under for <paramref name="prefix"/>, or null for none.</summary>
    public string? RunTagUnder(string? prefix) => prefix is null ? null : $"{prefix}/{Name}";

    /// <summary>
    /// Where the SDK puts the host: <c>{project dir}/bin/{configuration}/{framework}/{project}</c>,
    /// with <c>.exe</c> on Windows — the same convention every repository's build resolves.
    /// </summary>
    public static string ExecutableFor(GateProject project, string configuration, string framework)
    {
        var host = Path.Combine(project.Directory, "bin", configuration, framework, project.Name);
        return OperatingSystem.IsWindows() ? host + ".exe" : host;
    }

    public override string ToString() => Name;
}

/// <summary>What the default build step, or a caller's <see cref="GateDeclaration.Build"/>, is asked to build.</summary>
/// <param name="Solution">The solution the declaration named, or null to build each project.</param>
/// <param name="Projects">The projects the gate runs, in declaration order.</param>
/// <param name="Frameworks">The frameworks the gate runs, newest first.</param>
/// <param name="Configuration">The build configuration the hosts are found under.</param>
public sealed record GateBuild(
    string? Solution,
    IReadOnlyList<GateProject> Projects,
    IReadOnlyList<string> Frameworks,
    string Configuration);
