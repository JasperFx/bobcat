using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bobcat.Ui.Runs;
using Wolverine;
using Wolverine.Http;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop;
using Wolverine.Util;

namespace Bobcat.Ui.Runners;

/// <summary>A person asks a runner for a run (stoat#77). Exactly one of <see cref="Specs"/>,
/// <see cref="Feature"/> or <see cref="All"/> — or <see cref="Restart"/> for a new runner.</summary>
/// <param name="All">Every specification the runner registered (bobcat#645): the whole project.
/// Resolved here to those identities, as a feature is, so the runner still receives identities and
/// its refusal of a command naming none is unchanged.</param>
public record RunnerCommandRequest(
    IReadOnlyList<string>? Specs = null,
    string? Feature = null,
    string? Mode = null,
    bool Restart = false,
    string? By = null,
    bool All = false);

public record RunnerCommandQueued(Guid CommandId, string Kind, IReadOnlyList<string> Specs, string Mode);

/// <summary>
/// The runner wire and the commands people send over it (stoat#77; the design is stoat#79, the wire
/// settled by the spike in stoat#84).
/// </summary>
/// <remarks>
/// <para>
/// The wire's own endpoints — the inbound events and the command stream — are NOT license-gated, the
/// same as ingestion: Bobcat is MIT, and its runner must work against any monitor. The person's
/// view of the runners and the command endpoint are, by <c>CoordinationLicenseGate</c> (stoat#8),
/// and the command endpoint keeps its own guard besides.
/// </para>
/// <para>
/// ⚠️ Inbound CloudEvents pass <see cref="RunnerWire.Inbound"/> before anything maps them. Wolverine's
/// mapper resolves a <c>type</c> against every message type this host handles, so on an
/// unauthenticated port, mapping first would let any local page invoke any Stoat command.
/// </para>
/// </remarks>
public static class RunnerEndpoints
{
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>The runner wire's one inbound endpoint: registration, acknowledgements, and
    /// <c>stoat runner</c>'s status reports, each a structured-mode CloudEvent.</summary>
    [WolverinePost("/api/runners/events")]
    public static async Task<IResult> Receive(HttpRequest request, [NotBody] IMessageBus bus, [NotBody] IWolverineRuntime runtime)
    {
        var ct = request.HttpContext.RequestAborted;

        var mediaType = request.ContentType?.Split(';')[0].Trim();
        if (mediaType is not (RunnerWire.ContentType or "application/json"))
            return Results.Problem($"expected {RunnerWire.ContentType}", statusCode: StatusCodes.Status415UnsupportedMediaType);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(await new StreamReader(request.Body).ReadToEndAsync(ct));
        }
        catch (JsonException e)
        {
            return refuse($"not a JSON CloudEvent: {e.Message}");
        }

        // The allowlist, BEFORE mapping: a type not on it is never even deserialized.
        var type = node is JsonObject obj && obj["type"] is JsonValue value && value.TryGetValue<string>(out var t) ? t : null;
        if (type is null || !RunnerWire.Inbound.TryGetValue(type, out var expected))
        {
            return refuse($"'{type ?? "(no type)"}' is not a runner-wire event — accepted: {string.Join(", ", RunnerWire.Inbound.Keys)}");
        }

        var envelope = new Envelope();
        try
        {
            mapperFor(runtime).MapIncoming(envelope, node);
        }
        catch (Exception e) when (e is JsonException or UnknownMessageTypeNameException or InvalidOperationException)
        {
            return refuse($"could not read the '{type}' event: {e.Message}");
        }

        if (envelope.Message?.GetType() != expected) return refuse($"the '{type}' event carried no readable data");

        await bus.InvokeAsync(envelope.Message, ct);
        return Results.Accepted();
    }

    /// <summary>
    /// The runner's command stream: an ordinary endpoint returning <see cref="IResult"/>, as
    /// Wolverine's streaming docs describe, held open for as long as the runner listens. Each command
    /// is a CloudEvent in <c>data</c>, with its type as the SSE <c>event</c> and its id as the SSE
    /// <c>id</c>, which is what a reconnect's <c>Last-Event-ID</c> resumes from.
    /// </summary>
    [WolverineGet("/api/runners/{runnerId}/commands")]
    public static IResult Commands(string runnerId, HttpContext context, RunnerRegistry runners, IWolverineRuntime runtime)
    {
        if (!runners.Exists(runnerId))
            return Results.NotFound(new { errors = new[] { $"no runner '{runnerId}' — register it first" } });

        Guid? lastEventId = Guid.TryParse(context.Request.Headers["Last-Event-ID"].FirstOrDefault(), out var id) ? id : null;
        var mapper = mapperFor(runtime);

        return TypedResults.ServerSentEvents(
            events(runners.Stream(runnerId, lastEventId, KeepAlive, context.RequestAborted), mapper));
    }

    /// <summary>
    /// A person asks a runner for a run, or for a new runner (stoat#78's buttons). A runner only ever
    /// receives identities, and every one has to be in what the runner registered, or nothing is sent.
    /// </summary>
    [WolverinePost("/api/runners/{runnerId}/commands")]
    public static IResult Ask(
        string runnerId, RunnerCommandRequest request, RunnerRegistry runners)
    {
        var registered = runners.SpecsOf(runnerId);
        if (registered is null) return Results.NotFound(new { errors = new[] { $"no runner '{runnerId}'" } });

        var by = string.IsNullOrWhiteSpace(request.By) ? "bobcat-ui" : request.By.Trim();

        if (request.Restart)
        {
            var restart = runners.Enqueue(runnerId, "restart", [], RunnerWire.Cold, by);
            return Results.Accepted(value: new RunnerCommandQueued(restart, "restart", [], RunnerWire.Cold));
        }

        var sources = (request.Specs is { Count: > 0 } ? 1 : 0) + (request.Feature is not null ? 1 : 0) + (request.All ? 1 : 0);
        if (sources != 1) return refuse("name exactly one of specs, feature or all — or restart");

        IReadOnlyList<string> specs;
        if (request.Specs is { Count: > 0 } named)
        {
            specs = named.Distinct().ToList();
        }
        else if (request.All)
        {
            specs = registered.Distinct().ToList();
            if (specs.Count == 0) return refuse("this runner registered no specifications to run");
        }
        else
        {
            var feature = request.Feature!;
            specs = registered.Where(x => x.StartsWith(feature + "/", StringComparison.Ordinal)).ToList();
            if (specs.Count == 0) return refuse($"this runner has no specs in feature '{feature}'");
        }

        var unknown = specs.Where(x => !registered.Contains(x, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0) return refuse(unknownSpecs(unknown, registered));

        var mode = string.IsNullOrWhiteSpace(request.Mode) ? RunnerWire.Cold : request.Mode.Trim().ToLowerInvariant();
        if (!(runners.ModesOf(runnerId) ?? []).Contains(mode))
            return refuse($"this runner does not offer '{mode}' runs — it offers {string.Join(", ", runners.ModesOf(runnerId) ?? [])}");

        var commandId = runners.Enqueue(runnerId, "run", specs, mode, by);
        return Results.Accepted(value: new RunnerCommandQueued(commandId, "run", specs, mode));
    }

    /// <summary>The runners and their recent commands, each command linked to the run it produced
    /// once that run's <c>run_started</c> names it (JasperFx/bobcat#392).</summary>
    [WolverineGet("/api/runners")]
    public static IReadOnlyList<RunnerView> All(RunnerRegistry runners, MonitorRunRegistry runs)
        => runners.All(runLookup(runs));

    /// <summary>
    /// Which runners can run this (stoat#78): <c>?spec=</c>, repeated, <c>?feature=</c>, or
    /// <c>?all=true</c> for a whole project (every runner that registered anything, bobcat#645).
    /// Empty is an answer — the button says no runner can, and how to start one — never a 404.
    /// </summary>
    [WolverineGet("/api/runners/for")]
    public static IReadOnlyList<RunnerView> For(HttpRequest request, RunnerRegistry runners, MonitorRunRegistry runs)
    {
        if (bool.TryParse(request.Query["all"].FirstOrDefault(), out var all) && all)
            return runners.All(runLookup(runs)).Where(x => x.Specs > 0).ToList();

        var specs = request.Query["spec"].Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().ToList();
        var feature = request.Query["feature"].FirstOrDefault();
        return runners.CandidatesFor(specs, string.IsNullOrWhiteSpace(feature) ? null : feature, runLookup(runs));
    }

    [WolverineGet("/api/runners/{runnerId}")]
    public static RunnerView? Find(string runnerId, RunnerRegistry runners, MonitorRunRegistry runs)
        => runners.Find(runnerId, runLookup(runs));

    /// <summary>
    /// What a runner can run, spec by spec with its planned steps (bobcat#644): the runner page, which
    /// is how a suite is previewed in the UI before anything has run.
    /// </summary>
    [WolverineGet("/api/runners/{runnerId}/specs")]
    public static IResult Specifications(string runnerId, RunnerRegistry runners)
        => runners.SpecificationsOf(runnerId) is { } specs
            ? Results.Ok(specs)
            : Results.NotFound(new { errors = new[] { $"no runner '{runnerId}'" } });

    private static Func<Guid, Guid?> runLookup(MonitorRunRegistry runs)
    {
        var byCommand = runs.All()
            .Where(r => r.Command is not null)
            .GroupBy(r => r.Command!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAt).First().RunId, StringComparer.OrdinalIgnoreCase);
        return commandId => byCommand.TryGetValue(commandId.ToString(), out var runId) ? runId : null;
    }

    private static async IAsyncEnumerable<SseItem<string>> events(IAsyncEnumerable<object?> stream, CloudEventsMapper mapper)
    {
        await foreach (var message in stream)
        {
            if (message is null)
            {
                yield return new SseItem<string>("{}", RunnerWire.KeepAliveEvent);
                continue;
            }

            var commandId = message switch
            {
                RunSpecsCommand run => run.CommandId,
                RestartRunnerCommand restart => restart.CommandId,
                _ => throw new InvalidOperationException($"not a runner command: {message.GetType().Name}")
            };

            var envelope = new Envelope
            {
                Id = commandId,
                Message = message,
                MessageType = message.GetType().ToMessageTypeName(),
                Source = RunnerWire.Source,
                SentAt = DateTimeOffset.UtcNow,
            };

            yield return new SseItem<string>(mapper.WriteToString(envelope), envelope.MessageType) { EventId = commandId.ToString() };
        }
    }

    // The handler graph is public on the concrete runtime only (stoat#84's caveat 4).
    private static CloudEventsMapper mapperFor(IWolverineRuntime runtime)
        => new(((WolverineRuntime)runtime).Handlers, RunnerWire.Json);

    private static IResult refuse(string error) => Results.BadRequest(new { errors = new[] { error } });

    /// <summary>
    /// Why the runner cannot run these (bobcat#652). An identity in a feature this runner has is from
    /// this suite, renamed or removed since the run a page shows — pressing Run on an earlier run's
    /// page after a rebuild — and blaming "another suite" for it sent people looking for a runner that
    /// does not exist. Only an identity in no feature of this runner's is someone else's.
    /// </summary>
    private static string unknownSpecs(IReadOnlyList<string> unknown, IReadOnlyList<string> registered)
    {
        static string featureOf(string identity) => identity.LastIndexOf('/') is var slash and > 0 ? identity[..slash] : "";

        var features = registered.Select(featureOf).ToHashSet(StringComparer.Ordinal);
        var gone = unknown.Where(x => features.Contains(featureOf(x))).ToList();
        var foreign = unknown.Except(gone).ToList();

        var reasons = new List<string>();
        if (gone.Count > 0)
            reasons.Add($"this runner no longer specifies {string.Join("; ", gone)} — renamed or removed since that run; run its feature, or the spec under its new name");
        if (foreign.Count > 0)
            reasons.Add($"not this runner's specs: {string.Join("; ", foreign)} — a slice or spec from another suite runs on that suite's runner");

        return string.Join(". ", reasons);
    }
}
