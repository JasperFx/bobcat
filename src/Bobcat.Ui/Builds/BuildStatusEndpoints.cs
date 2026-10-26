using Wolverine.Http;

namespace Bobcat.Ui.Builds;

/// <summary>
/// Whether a watched spec project builds (bobcat#651), as <c>bobcat watch</c> reports it on each
/// rebuild. The receiver's copy of the publisher's <c>BuildStatusReport</c>: the shape on the wire is
/// the contract, as it is for the monitor events.
/// </summary>
/// <param name="State"><c>building</c>, <c>build-failed</c> or <c>built</c>.</param>
/// <param name="Detail">The first compiler errors of a failed build.</param>
public record BuildStatusReported(
    string Suite,
    string? Repository,
    string? Branch,
    string State,
    string? Detail,
    DateTimeOffset At);

/// <summary>
/// The latest build report per project (bobcat#651), in memory: a watch re-reports on its next
/// rebuild, so a console restart loses nothing a person would miss for long.
/// </summary>
public sealed class BuildStatusRegistry
{
    public const string Building = "building";
    public const string BuildFailed = "build-failed";
    public const string Built = "built";

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Repository, string Suite), BuildStatusReported> _latest = new();

    /// <summary>Record a report, unless a later one for the same project is already in.</summary>
    public void Record(BuildStatusReported report)
    {
        var key = (normalize(report.Repository), report.Suite);
        lock (_gate)
        {
            if (_latest.TryGetValue(key, out var existing) && existing.At > report.At) return;
            _latest[key] = report;
        }
    }

    /// <summary>Each project's latest report, newest first.</summary>
    public IReadOnlyList<BuildStatusReported> All()
    {
        lock (_gate) return _latest.Values.OrderByDescending(x => x.At).ToList();
    }

    private static string normalize(string? path) => (path ?? "").Replace('\\', '/').TrimEnd('/');
}

public static class BuildStatusEndpoints
{
    /// <summary>
    /// <c>bobcat watch</c> says how its rebuild went (bobcat#651). Not on the ingest stream, because a
    /// build belongs to no run; a console that does not serve this answers 404, and the publisher
    /// shrugs.
    /// </summary>
    [WolverinePost("/api/build-status")]
    public static IResult Report(BuildStatusReported report, BuildStatusRegistry builds, SignalRBatchAccumulator accumulator)
    {
        if (string.IsNullOrWhiteSpace(report.Suite) || report.State is not (BuildStatusRegistry.Building or BuildStatusRegistry.BuildFailed or BuildStatusRegistry.Built))
        {
            return Results.BadRequest(new { errors = new[] { "a build report names its suite and a state of building, build-failed or built" } });
        }

        builds.Record(report);
        // Pushed as the whole list (bobcat#784), the shape GET serves, so the banner replaces what it
        // has instead of polling for it.
        accumulator.Notify(new Contracts.BuildStatusChanged(builds.All()));
        return Results.Accepted();
    }

    /// <summary>Each watched project's latest build, newest first: what the board's banner reads.</summary>
    [WolverineGet("/api/build-status")]
    public static IReadOnlyList<BuildStatusReported> All(BuildStatusRegistry builds) => builds.All();
}
