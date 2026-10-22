using System.Diagnostics;

namespace Bobcat.Supervisor;

/// <summary>
/// Runs a <see cref="GateDeclaration"/>: preflight every track a unit names, build once, print the
/// plan, run one <see cref="Supervisor"/> per unit under the track and budget rules, record each
/// unit's duration for next time, and report (bobcat#729).
/// </summary>
/// <remarks>
/// <para>
/// <b>Report, don't act</b> beyond scheduling. The gate's exit code is the worst of its units in
/// the supervisor's own convention — 2 over 1 over 0 — a pass-on-retry is reported as flaky and
/// never folded into clean, and nothing is retried that the project's own policy would not retry.
/// Every unit runs: a red unit does not cancel the rest, because a full report is the point of a
/// gate.
/// </para>
/// <para>
/// <b>Each unit is its own run on the monitor</b>, published under
/// <c>{RunTagPrefix}/{project}.{framework}</c> through <see cref="Supervisor.RunTag"/> (bobcat#768),
/// so a board shows one card per unit under the plan node that asked for the gate.
/// </para>
/// </remarks>
public sealed class Gate
{
    private readonly GateDeclaration _declaration;
    private readonly object _log = new();

    public Gate(GateDeclaration declaration) => _declaration = declaration;

    public async Task<GateResults> Run(CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var budget = Math.Max(1, _declaration.WorkerBudget);

        var units = _declaration.Units();
        var durations = _declaration.DurationsPath is { } durationsPath
            ? GateDurations.Load(durationsPath)
            : GateDurations.Empty();
        var plan = GateScheduler.Order(units, durations.Predict);

        // 1. Preflight, by track, before anything is built or launched: a stopped container is
        //    reported in seconds and by name, not ten minutes in as a thousand identical failures.
        var preflight = await runPreflight(units, ct);
        if (preflight.Any(p => !p.Results.Succeeded()))
        {
            var failed = preflight.Where(p => !p.Results.Succeeded()).Select(p => p.Track.Name);
            return finish(plan, [], preflight, budget, clock,
                $"preflight failed for track(s) {string.Join(", ", failed)}; nothing ran");
        }

        // 2. Build once. The hosts then run as built, and the bootstrap is paid once per gate
        //    rather than once per suite.
        try
        {
            await build(units, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return finish(plan, [], preflight, budget, clock, $"the build failed: {e.Message}");
        }

        var missing = units.Where(u => !File.Exists(u.Executable)).ToArray();
        if (missing.Length > 0)
        {
            return finish(plan, [], preflight, budget, clock,
                "no test host at " + string.Join("; ", missing.Select(u => $"{u.Executable} ({u.Name})")) +
                " — a project built without UseMicrosoftTestingPlatformRunner yields no supervisable host at that path");
        }

        // 3. The plan, printed before anything runs, so predicted can be set against actual after.
        log($"Gate: {units.Count} unit(s), {budget} worker(s) budgeted, frameworks {_declaration.Frameworks}" +
            (_declaration.RunTagPrefix is { } prefix ? $", publishing under {prefix}/<unit>" : ""));
        var width = plan.Max(u => u.Name.Length);
        for (var i = 0; i < plan.Count; i++)
        {
            var unit = plan[i];
            log($"  {i + 1,3}. {unit.Name.PadRight(width)}  {GateScheduler.WorkersFor(unit, budget)} worker(s)" +
                $"  tracks: {(unit.Tracks.Count == 0 ? "(none)" : string.Join(", ", unit.Tracks.Select(t => t.Name)))}" +
                $"  predicted: {(durations.Predict(unit) is { } p ? GateResults.Humanize(p) : "first run")}");
        }

        // 4. Run, longest first, never two on a track, never past the budget.
        var results = new List<GateUnitResult>();
        await GateScheduler.Run(plan, budget, async (unit, workers, token) =>
        {
            var result = await runUnit(unit, workers, durations.Predict(unit), token);
            lock (results) results.Add(result);
        }, ct);

        // 5. Remember what each unit took, for next time's order.
        if (_declaration.DurationsPath is { } path)
        {
            foreach (var result in results.Where(r => r.Results is not null)) durations.Record(result.Unit.Name, result.Duration);

            try
            {
                durations.Save(path);
            }
            catch (Exception e)
            {
                log($"Could not write the gate's duration record to {path}: {e.Message}");
            }
        }

        return finish(plan, results, preflight, budget, clock, abortReason: null);
    }

    private GateResults finish(
        IReadOnlyList<GateUnit> plan,
        IReadOnlyList<GateUnitResult> ran,
        IReadOnlyList<TrackPreflight> preflight,
        int budget,
        Stopwatch clock,
        string? abortReason)
    {
        var started = ran.ToDictionary(r => r.Unit.Name, StringComparer.Ordinal);
        var all = ran.ToList();
        all.AddRange(plan.Where(u => !started.ContainsKey(u.Name))
            .Select(u => new GateUnitResult(u, null, TimeSpan.Zero, null, 0, u.RunTagUnder(_declaration.RunTagPrefix))));

        var results = new GateResults
        {
            Units = all,
            Preflight = preflight,
            Plan = plan,
            AbortReason = abortReason,
            WorkerBudget = budget,
            Duration = clock.Elapsed
        };

        log(results.ToText());
        return results;
    }

    private async Task<IReadOnlyList<TrackPreflight>> runPreflight(IReadOnlyList<GateUnit> units, CancellationToken ct)
    {
        var used = units.SelectMany(u => u.Tracks).Distinct().ToHashSet();
        var results = new List<TrackPreflight>();

        foreach (var track in _declaration.Tracks.Where(used.Contains))
        {
            if (track.Preflight.IsEmpty) continue;

            var outcome = await track.Preflight.Run(ct);
            results.Add(new TrackPreflight(track, outcome));

            if (!outcome.Succeeded())
            {
                log($"✗ track {track.Name} failed preflight:");
                log(Bobcat.Runtime.Preflight.Describe(outcome));
            }
        }

        return results;
    }

    private async Task build(IReadOnlyList<GateUnit> units, CancellationToken ct)
    {
        if (_declaration.SkipBuild) return;

        var frameworks = units.Select(u => u.Framework)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => f, TargetFrameworks.Comparer)
            .ToArray();

        var request = new GateBuild(_declaration.Solution, _declaration.Projects, frameworks, _declaration.Configuration);

        if (_declaration.Build is { } custom)
        {
            await custom(request, ct);
            return;
        }

        var targets = request.Solution is { } solution ? [solution] : request.Projects.Select(p => p.Path).ToArray();
        foreach (var framework in frameworks)
        {
            foreach (var target in targets)
            {
                await dotnetBuild(target, framework, ct);
            }
        }
    }

    private async Task dotnetBuild(string target, string framework, CancellationToken ct)
    {
        var arguments = $"build \"{target}\" -c {_declaration.Configuration} -f {framework} --nologo -v quiet";
        log($"dotnet {arguments}");

        var info = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // One build at a time, and never a lingering node that outlives the gate.
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(info) ?? throw new InvalidOperationException("dotnet could not be started.");

        var tail = new Queue<string>();
        void keep(string? line)
        {
            if (line is null) return;
            log($"  {line}");
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 20) tail.Dequeue();
            }
        }

        process.OutputDataReceived += (_, e) => keep(e.Data);
        process.ErrorDataReceived += (_, e) => keep(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string[] lines;
            lock (tail) lines = tail.ToArray();
            throw new InvalidOperationException(
                $"dotnet build {Path.GetFileName(target)} -f {framework} exited {process.ExitCode}:{Environment.NewLine}" +
                string.Join(Environment.NewLine, lines));
        }
    }

    private async Task<GateUnitResult> runUnit(GateUnit unit, int workers, TimeSpan? predicted, CancellationToken ct)
    {
        var tag = unit.RunTagUnder(_declaration.RunTagPrefix);
        var clock = Stopwatch.StartNew();
        log($"=== {unit.Name}: starting on {workers} worker(s)" +
            (unit.Tracks.Count > 0 ? $", holding {string.Join(", ", unit.Tracks.Select(t => t.Name))}" : "") + " ===");

        try
        {
            var supervisor = supervisorFor(unit, workers, tag);
            var results = await supervisor.Run(ct);
            var result = new GateUnitResult(unit, results, clock.Elapsed, predicted, workers, tag);
            log($"=== {unit.Name}: {result.Mark} {result.Summary} in {GateResults.Humanize(result.Duration)} ===");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The supervisor itself threw — a host that would not start, a factory that failed.
            // The unit is Indeterminate with the exception, and the rest of the gate runs.
            log($"=== {unit.Name}: ? {e.GetType().Name}: {e.Message} ===");
            return new GateUnitResult(unit, null, clock.Elapsed, predicted, workers, tag, e);
        }
    }

    private Supervisor supervisorFor(GateUnit unit, int workers, string? tag)
    {
        var factory = (unit.Project.WorkerFactory ?? _declaration.WorkerFactory)?.Invoke(unit)
                      ?? new MtpWorkerFactory(unit.Executable);

        var supervisor = new Supervisor(factory)
        {
            MaxParallelWorkers = workers,
            PublishToMonitor = _declaration.PublishToMonitor,
            MonitorSink = _declaration.MonitorSink,
            RunTag = tag,
            Log = message => log($"[{unit.Name}] {message}")
        };

        _declaration.Configure?.Invoke(unit, supervisor);
        unit.Project.Configure?.Invoke(unit, supervisor);

        // The budget is the gate's: a project may run on fewer lanes than it was charged for,
        // never more.
        if (supervisor.MaxParallelWorkers > workers)
        {
            log($"[{unit.Name}] {supervisor.MaxParallelWorkers} workers asked for, {workers} allowed by the budget");
            supervisor.MaxParallelWorkers = workers;
        }

        return supervisor;
    }

    private void log(string message)
    {
        lock (_log) _declaration.Log(message);
    }
}
