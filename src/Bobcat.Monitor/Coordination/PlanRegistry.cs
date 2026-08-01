namespace Bobcat.Monitor.Coordination;

public enum PlanSource
{
    /// <summary>Loaded from a file in the plans directory — the source-controlled artifact.</summary>
    File,

    /// <summary>Pushed over HTTP (PUT /api/plans/{slug}) — transient until removed.</summary>
    Pushed
}

/// <summary>
/// One plan as the registry knows it. An invalid document is registered WITH its errors
/// rather than dropped — a plan file with a typo should render as broken on the dashboard,
/// not silently vanish from it.
/// </summary>
public record RegisteredPlan(
    string Slug,
    PlanSource Source,
    PlanDocument? Document,
    string? SourcePath,
    DateTimeOffset LoadedAt,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Document is not null;
}

public record RescanResult(int Valid, int Invalid, int Removed);

public record PushResult(RegisteredPlan? Plan, IReadOnlyList<string> Errors, bool FileOwned)
{
    public bool Succeeded => Plan is not null;
}

public enum RemovePlanResult
{
    Removed,
    NotFound,

    /// <summary>File-backed plans can't be removed over HTTP — a rescan would just resurrect
    /// them. Delete or move the file instead.</summary>
    FileOwned
}

/// <summary>
/// The monitor's view of every plan document it has been given
/// (docs/agent-coordination-design.md). Deliberately NOT archive-backed like
/// <see cref="Runs.MonitorRunRegistry"/>: plan documents are source-controlled files — the
/// declared intent lives in git, so the registry is a reloadable cache of parsed
/// declarations, not a store. Status history (what happened to the declared work) is the
/// event-sourced part of the coordination context and lands with the SQLite event store.
/// </summary>
/// <remarks>
/// Two sources, one identity space (the plan slug), with a strict precedence: <b>a file
/// always beats a push</b>. A pushed plan whose slug a file later claims is replaced on
/// rescan, and a push against a file-owned slug is refused outright — otherwise the registry
/// and the git artifact would disagree about the same plan until the next rescan, each
/// looking authoritative. Plans directory: <c>Monitor:PlansPath</c> configuration, then the
/// <c>BOBCAT_MONITOR_PLANS</c> environment variable, then <c>~/.bobcat/monitor/plans</c>.
/// No SignalR notifications yet on purpose — the live-update contract gets designed as one
/// piece with the DAG view.
/// </remarks>
public sealed class PlanRegistry
{
    public const string PlansPathVariable = "BOBCAT_MONITOR_PLANS";

    private readonly string _plansPath;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RegisteredPlan> _plans = new();

    public PlanRegistry(string? plansPath = null)
    {
        _plansPath = plansPath
                     ?? Environment.GetEnvironmentVariable(PlansPathVariable)
                     ?? Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         ".bobcat", "monitor", "plans");

        Directory.CreateDirectory(_plansPath);
        Rescan();
    }

    public string PlansPath => _plansPath;

    /// <summary>
    /// Resync the file-sourced plans with the directory: every *.yaml/*.yml is (re)parsed,
    /// and file plans whose files are gone drop out. Pushed plans are untouched — unless a
    /// file now claims their slug, in which case the file wins.
    /// </summary>
    public RescanResult Rescan()
    {
        lock (_gate)
        {
            var before = _plans.Where(x => x.Value.Source == PlanSource.File).Select(x => x.Key).ToHashSet();
            foreach (var key in before) _plans.Remove(key);

            var valid = 0;
            var invalid = 0;

            // Deterministic order so a slug declared by two files always resolves the same
            // way: the first file (ordinally by name) owns the slug, the second registers
            // as an error entry.
            var files = Directory.EnumerateFiles(_plansPath, "*.yaml")
                .Concat(Directory.EnumerateFiles(_plansPath, "*.yml"))
                .OrderBy(x => x, StringComparer.Ordinal);

            foreach (var file in files)
            {
                var registered = loadFile(file);
                if (registered.IsValid) valid++;
                else invalid++;
            }

            var removed = before.Count(key =>
                !_plans.TryGetValue(key, out var now) || now.Source != PlanSource.File);

            return new RescanResult(valid, invalid, removed);
        }
    }

    /// <summary>
    /// Register a document pushed over HTTP. Invalid documents are refused with their errors
    /// (the pusher is right there to hear them — unlike a file, where the brokenness has to
    /// surface on the dashboard), and file-owned slugs are refused outright.
    /// </summary>
    public PushResult Push(string slug, string yaml)
    {
        var result = PlanParser.Parse(yaml);
        if (!result.Succeeded) return new PushResult(null, result.Errors, FileOwned: false);

        var document = result.Document!;
        if (document.Plan != slug)
        {
            return new PushResult(
                null, [$"the document declares plan '{document.Plan}' but was pushed to '{slug}'"], FileOwned: false);
        }

        lock (_gate)
        {
            if (_plans.TryGetValue(slug, out var existing) && existing.Source == PlanSource.File)
            {
                return new PushResult(
                    null,
                    [$"plan '{slug}' is owned by file {existing.SourcePath} — edit the file and rescan"],
                    FileOwned: true);
            }

            var plan = new RegisteredPlan(slug, PlanSource.Pushed, document, null, DateTimeOffset.UtcNow, []);
            _plans[slug] = plan;
            return new PushResult(plan, [], FileOwned: false);
        }
    }

    public RemovePlanResult Remove(string slug)
    {
        lock (_gate)
        {
            if (!_plans.TryGetValue(slug, out var existing)) return RemovePlanResult.NotFound;
            if (existing.Source == PlanSource.File) return RemovePlanResult.FileOwned;

            _plans.Remove(slug);
            return RemovePlanResult.Removed;
        }
    }

    public RegisteredPlan? Find(string slug)
    {
        lock (_gate) return _plans.GetValueOrDefault(slug);
    }

    public IReadOnlyList<RegisteredPlan> All()
    {
        lock (_gate) return _plans.Values.OrderBy(x => x.Slug, StringComparer.Ordinal).ToList();
    }

    private RegisteredPlan loadFile(string file)
    {
        string yaml;
        try
        {
            yaml = File.ReadAllText(file);
        }
        catch (Exception e)
        {
            return register(freeKey(fileStem(file)), null, file, [$"could not read {file}: {e.Message}"]);
        }

        var result = PlanParser.Parse(yaml);
        if (!result.Succeeded)
        {
            // Keyed by filename stem — an unparseable document may not even have a slug.
            return register(freeKey(fileStem(file)), null, file, result.Errors);
        }

        var document = result.Document!;
        if (_plans.TryGetValue(document.Plan, out var existing))
        {
            if (existing.Source == PlanSource.File)
            {
                return register(freeKey(fileStem(file)), null, file,
                    [$"plan slug '{document.Plan}' is already declared by {existing.SourcePath}"]);
            }

            // A pushed plan loses its slug to the source-controlled artifact.
            _plans.Remove(document.Plan);
        }

        return register(document.Plan, document, file, []);
    }

    private RegisteredPlan register(string key, PlanDocument? document, string path, IReadOnlyList<string> errors)
    {
        var plan = new RegisteredPlan(key, PlanSource.File, document, path, DateTimeOffset.UtcNow, errors);
        _plans[key] = plan;
        return plan;
    }

    private static string fileStem(string file) => Path.GetFileNameWithoutExtension(file);

    /// <summary>An error entry's filename-stem key could collide with a real slug from
    /// another file; suffix rather than overwrite — every broken file deserves its own row.</summary>
    private string freeKey(string stem)
    {
        if (!_plans.ContainsKey(stem)) return stem;

        var n = 2;
        while (_plans.ContainsKey($"{stem}-{n}")) n++;
        return $"{stem}-{n}";
    }
}
