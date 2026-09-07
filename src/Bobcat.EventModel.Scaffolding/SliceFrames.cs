using JasperFx.CodeGeneration;

namespace Bobcat.EventModel.Scaffolding;

/// <summary>A positional record declaration, fields synthesized from the model's hints.</summary>
public class RecordFrame : ScaffoldFrame
{
    private readonly string _name;
    private readonly IReadOnlyList<(string Type, string Name)> _fields;
    private readonly string? _docComment;
    private readonly IReadOnlyList<string> _warnings;

    public RecordFrame(string name, IReadOnlyList<(string Type, string Name)> fields, string? docComment = null,
        IReadOnlyList<string>? warnings = null)
    {
        _name = name;
        _fields = fields;
        _docComment = docComment;
        _warnings = warnings ?? [];
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        foreach (var warning in _warnings)
        {
            writer.WriteLine($"// WARNING (from the model): {warning}");
        }

        if (_docComment is not null)
        {
            var lines = _docComment.Split('\n');
            if (lines.Length == 1)
            {
                writer.WriteLine($"/// <summary>{_docComment}</summary>");
            }
            else
            {
                writer.WriteLine("/// <summary>");
                foreach (var line in lines)
                {
                    writer.WriteLine($"/// {line}");
                }

                writer.WriteLine("/// </summary>");
            }
        }

        var fields = string.Join(", ", _fields.Select(x => $"{x.Type} {x.Name}"));
        writer.WriteLine($"public record {_name}({fields});");
        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// The self-aggregating write model: Create for the first event, Apply per event — the only
/// mutators, owned by the store.
/// </summary>
public class AggregateFrame : ScaffoldFrame
{
    private readonly string _name;
    private readonly IReadOnlyList<string> _events;
    private readonly IReadOnlyList<(string Type, string Name)> _fields;

    public AggregateFrame(string name, IReadOnlyList<string> events, IReadOnlyList<(string Type, string Name)> fields)
    {
        _name = name;
        _events = events;
        _fields = fields;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"BLOCK:public class {_name}");
        writer.WriteLine("public Guid Id { get; set; }");
        foreach (var (type, fieldName) in _fields.Where(x => x.Name != "Id"))
        {
            writer.WriteLine($"public {type} {fieldName} {{ get; set; }}");
        }

        var first = true;
        foreach (var @event in _events)
        {
            var argument = char.ToLowerInvariant(@event[0]) + @event[1..];
            writer.BlankLine();

            if (first)
            {
                writer.Write($"BLOCK:public static {_name} Create({@event} {argument})");
                writer.WriteLine("// TODO: fold the creating event into the initial state");
                writer.WriteLine($"return new {_name}();");
                writer.FinishBlock();
                first = false;
                writer.BlankLine();
            }

            writer.Write($"BLOCK:public void Apply({@event} {argument})");
            writer.WriteLine("// TODO: fold this event into the state. Deterministic only —");
            writer.WriteLine("// timestamps belong on the event record, never DateTimeOffset.UtcNow here.");
            writer.FinishBlock();
        }

        writer.FinishBlock();
        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// The command/automation handler skeleton: aggregate handler workflow, a <c>[WriteModel]</c>
/// parameter, an <c>EventsToAppend</c> return — the same shapes the runtime's own event-capture
/// frames will compile once the code is real.
/// </summary>
public class WriteModelHandlerFrame : ScaffoldFrame
{
    private readonly CuratedSlice _slice;
    private readonly bool _maybeNewStream;
    private readonly IReadOnlyList<CascadedMessage> _cascaded;
    private readonly IReadOnlyList<string> _warnings;
    private readonly string? _publishedBy;

    public WriteModelHandlerFrame(CuratedSlice slice, bool maybeNewStream,
        IReadOnlyList<CascadedMessage>? cascaded = null, IReadOnlyList<string>? warnings = null,
        string? publishedBy = null)
    {
        _slice = slice;
        _maybeNewStream = maybeNewStream;
        _cascaded = cascaded ?? [];
        _warnings = warnings ?? [];
        _publishedBy = publishedBy;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var aggregate = SliceScaffolder.AggregateFor(_slice);
        var isAutomation = _slice.Pattern == "Automation";
        var trigger = isAutomation ? SliceScaffolder.TriggerFor(_slice) : _slice.Command ?? _slice.Name;

        writer.WriteLine("/// <summary>");
        writer.WriteLine(isAutomation
            ? $"/// Automation slice: triggered by the {trigger} event, never by a route. Decides and returns —"
            : "/// State-change slice: decides only \"is this request valid\" — every further consequence");
        writer.WriteLine(isAutomation
            ? "/// the framework loads the aggregate, appends, and commits. Design for at-least-once delivery."
            : "/// is a separate automation triggered by an event appended here.");
        if (_publishedBy is not null)
        {
            writer.WriteLine($"/// Triggered over the bus: the model shows slice '{_publishedBy}' publishing {trigger}.");
        }

        writer.WriteLine("/// </summary>");
        writer.Write($"BLOCK:public static class {_slice.Name}Handler");

        var parameter = _maybeNewStream ? $"[WriteModel] {aggregate}? " : $"[WriteModel] {aggregate} ";
        var argument = char.ToLowerInvariant(aggregate[0]) + aggregate[1..];
        var returnType = _cascaded.Count == 0
            ? "EventsToAppend"
            : $"(EventsToAppend, {string.Join(", ", _cascaded.Select(x => x.Name))})";
        writer.Write(
            $"BLOCK:public static {returnType} Handle({trigger} {(isAutomation ? "trigger" : "command")}, {parameter}{argument})");

        foreach (var hotspot in _slice.Hotspots)
        {
            writer.WriteLine($"// HOTSPOT (from the model): {hotspot}");
        }

        foreach (var warning in _warnings)
        {
            writer.WriteLine($"// WARNING (from the model): {warning}");
        }

        foreach (var refusal in refusals())
        {
            writer.WriteLine(
                $"// TODO guard: throw new InvalidOperationException(\"{refusal}\"); (asserted by `validation fails with`)");
        }

        writer.WriteLine("// The decision. Nothing to append is `return [];` — never a nullable event (wolverine#4309).");
        foreach (var message in _cascaded)
        {
            writer.WriteLine(message.LeavesTheSystem
                ? $"// {message.Name} leaves the system (outbound external edge); the cascade rides the transactional outbox."
                : $"// The model designates {message.Name} as bus-visible — slice '{message.HandledBy}' handles it; the cascade rides the transactional outbox.");
        }

        var events = string.Join(", ", _slice.Events.Select(x => $"new {x}(/* … */)"));
        var appended = events.Length > 0 ? $"[{events}]" : "[]";
        writeUnfilledDecision(writer,
            $"{_slice.Name} — decide which events this slice appends",
            _cascaded.Count == 0
                ? $"return {appended};"
                : $"return ({appended}, {string.Join(", ", _cascaded.Select(x => $"new {x.Name}(/* … */)"))});");
        writer.FinishBlock();
        writer.FinishBlock();
        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }

    private IEnumerable<string> refusals()
        => _slice.Specifications?.Scenarios
               .SelectMany(x => x.Then)
               .Select(x => x.ValidationFails)
               .OfType<string>()
               .Distinct()
           ?? [];
}

/// <summary>
/// The collapsed HTTP state-change slice — the default (CritterStackSamples#13 review, 2026-09-06):
/// the endpoint IS the handler. One transaction appends the events, the outbox carries anything
/// cascaded, the status code is honest, and a computed stream identity binds off the request body
/// via <c>[Identity]</c>. Refusals harvested from the slice's <c>validationFails</c> scenarios land
/// as TODOs in a <c>Validate</c> railway stub, not in the decision.
/// </summary>
public class CollapsedEndpointFrame : ScaffoldFrame
{
    private readonly CuratedSlice _slice;
    private readonly string _route;
    private readonly IReadOnlyList<CascadedMessage> _cascaded;
    private readonly IReadOnlyList<string> _warnings;

    public CollapsedEndpointFrame(CuratedSlice slice, string route,
        IReadOnlyList<CascadedMessage>? cascaded = null, IReadOnlyList<string>? warnings = null)
    {
        _slice = slice;
        _route = route;
        _cascaded = cascaded ?? [];
        _warnings = warnings ?? [];
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var command = _slice.Command ?? _slice.Name;
        var aggregate = SliceScaffolder.AggregateFor(_slice);
        var argument = char.ToLowerInvariant(aggregate[0]) + aggregate[1..];

        writer.WriteLine("/// <summary>");
        writer.WriteLine("/// The endpoint IS the handler: one transaction, honest status codes. Split a separate");
        writer.WriteLine("/// message handler out only when this command genuinely needs bus visibility — other");
        writer.WriteLine("/// callers, retry policies, scheduling — never for testability.");
        if (_cascaded.Count > 0)
        {
            writer.WriteLine($"/// The model designates {string.Join(", ", _cascaded.Select(x => x.Name))} as bus-visible; the cascade below rides this transaction's outbox.");
        }

        writer.WriteLine("/// </summary>");
        writer.Write($"BLOCK:public static class {_slice.Name}Endpoint");

        writer.Write($"BLOCK:public static ProblemDetails Validate({command}Request request)");
        var refusals = _slice.Specifications?.Scenarios
            .SelectMany(x => x.Then).Select(x => x.ValidationFails).OfType<string>().Distinct().ToList() ?? [];
        foreach (var refusal in refusals)
        {
            writer.WriteLine($"// TODO guard: return new ProblemDetails {{ Detail = \"{refusal}\", Status = 400 }};");
        }

        writer.WriteLine("return WolverineContinue.NoProblems;");
        writer.FinishBlock();
        writer.BlankLine();

        writer.WriteLine($"[WolverinePost(\"{_route}\")]");
        var returnType = $"({_slice.Name}Response, EventsToAppend{string.Concat(_cascaded.Select(x => $", {x.Name}"))})";
        writer.Write(
            $"BLOCK:public static {returnType} Post({command}Request request, [WriteModel] {aggregate}? {argument})");

        foreach (var hotspot in _slice.Hotspots)
        {
            writer.WriteLine($"// HOTSPOT (from the model): {hotspot}");
        }

        foreach (var warning in _warnings)
        {
            writer.WriteLine($"// WARNING (from the model): {warning}");
        }

        writer.WriteLine("// The decision. Nothing to append is `return (..., []);` — never a nullable event (wolverine#4309).");
        writer.WriteLine("// A computed stream id belongs on the request record: [Identity] public Guid ...Id => ...;");
        foreach (var message in _cascaded)
        {
            writer.WriteLine(message.LeavesTheSystem
                ? $"// {message.Name} leaves the system (outbound external edge); the cascade rides the transactional outbox."
                : $"// The model designates {message.Name} as bus-visible — slice '{message.HandledBy}' handles it; the cascade rides the transactional outbox.");
        }

        var events = string.Join(", ", _slice.Events.Select(x => $"new {x}(/* … */)"));
        var cascades = string.Concat(_cascaded.Select(x => $", new {x.Name}(/* … */)"));
        writeUnfilledDecision(writer,
            $"{_slice.Name} — decide which events this slice appends, and what to answer with",
            $"return (new {_slice.Name}Response(/* … */), [{events}]{cascades});");
        writer.FinishBlock();
        writer.FinishBlock();
        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// The two-hop OPT-IN: an endpoint translating the request into a cascaded, bus-visible command.
/// ⚠️ Not the default — use only when the command genuinely needs bus visibility (other callers,
/// retry/error policies, scheduling); the cascade means the response returns before the handler
/// runs, and creation semantics weaken to accepted-not-created. The model itself opts in
/// (issue #218) when an eventless HTTP slice publishes one message another slice handles off the
/// bus — that is the only way the scaffolder selects this shape.
/// </summary>
public class EndpointTranslationFrame : ScaffoldFrame
{
    private readonly CuratedSlice _slice;
    private readonly string _route;
    private readonly CascadedMessage? _cascadedCommand;
    private readonly IReadOnlyList<string> _warnings;

    public EndpointTranslationFrame(CuratedSlice slice, string route,
        CascadedMessage? cascadedCommand = null, IReadOnlyList<string>? warnings = null)
    {
        _slice = slice;
        _route = route;
        _cascadedCommand = cascadedCommand;
        _warnings = warnings ?? [];
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var request = _slice.Command ?? _slice.Name;
        var command = _cascadedCommand?.Name ?? request;

        if (_cascadedCommand is { HandledBy: { } handledBy })
        {
            writer.WriteLine("/// <summary>");
            writer.WriteLine($"/// Pure translation: the model designates {command} as bus-visible — slice '{handledBy}'");
            writer.WriteLine("/// handles it off the bus; this endpoint only mints identity and cascades.");
            writer.WriteLine("/// </summary>");
        }

        writer.Write($"BLOCK:public static class {_slice.Name}Endpoint");
        writer.WriteLine($"[WolverinePost(\"{_route}\")]");
        writer.Write($"BLOCK:public static (CreationResponse, {command}) Post({request}Request request)");
        foreach (var warning in _warnings)
        {
            writer.WriteLine($"// WARNING (from the model): {warning}");
        }

        writer.WriteLine("// Mint identity here at the edge (Guid.NewGuid(), or the slice's deterministic id");
        writer.WriteLine("// helper), then cascade the command — the cascade rides the transactional outbox.");
        writeUnfilledDecision(writer,
            $"{_slice.Name} — mint the identity and cascade {command}",
            "var id = Guid.NewGuid();",
            $"var command = new {command}(id /*, … from request */);",
            $"return (new CreationResponse($\"{_route}/{{id}}\"), command);");
        writer.FinishBlock();
        writer.FinishBlock();
        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>A read-model + projection + GET endpoint skeleton for a View slice.</summary>
public class ViewSliceFrame : ScaffoldFrame
{
    private readonly CuratedSlice _slice;

    public ViewSliceFrame(CuratedSlice slice)
    {
        _slice = slice;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var readModel = _slice.ReadModels.FirstOrDefault() ?? _slice.Name;
        var argument = char.ToLowerInvariant(readModel[0]) + readModel[1..];

        if (_slice.Projections.Count == 0)
        {
            // The projector-less View slice reads an entity's own snapshot back by id — the
            // write model IS the read model, so there is no duplicate class to emit and the
            // endpoint is [ReadAggregate]. That attribute applies ONLY to a single-stream
            // aggregation (a snapshot or live-aggregable type) — never to a projector-built or
            // fan-out document.
            writer.WriteLine($"// {readModel} is the entity's own Inline snapshot — the write model IS the read model.");
            writer.Write($"BLOCK:public static class Get{readModel}Endpoint");
            writer.WriteLine($"[WolverineGet(\"/api/{readModel.ToLowerInvariant()}/{{id}}\")]");
            writer.WriteLine("// [ReadAggregate] only applies to a single-stream aggregation; it 404s a missing stream.");
            writer.WriteLine($"public static {readModel} Get([ReadAggregate] {readModel} {argument}) => {argument};");
            writer.FinishBlock();
            writer.BlankLine();
            Next?.GenerateCode(method, writer);
            return;
        }

        writer.Write($"BLOCK:public class {readModel}");
        writer.WriteLine("public Guid Id { get; set; }");
        writer.WriteLine("// TODO: the projected columns the model's scenarios assert on");
        writer.FinishBlock();
        writer.BlankLine();

        writer.WriteLine("// Async lifecycle: register with the daemon RUNNING (AddAsyncDaemon), or this never advances.");
        if (_slice.FanOut)
        {
            writer.Write($"BLOCK:public class {_slice.Projections[0]} : MultiStreamProjection<{readModel}, Guid>");
            writer.Write($"BLOCK:public {_slice.Projections[0]}()");
            writer.WriteLine("// TODO: the fan-out routing — Identities<SourceEvent>(x => [x.OneId, x.OtherId]);");
            writer.FinishBlock();
            writer.BlankLine();
            writer.WriteLine("// TODO: Apply methods per source event");
            writer.FinishBlock();
        }
        else
        {
            writer.Write($"BLOCK:public class {_slice.Projections[0]} : SingleStreamProjection<{readModel}, Guid>");
            writer.WriteLine("// TODO: Apply methods per source event");
            writer.FinishBlock();
        }

        writer.BlankLine();

        // Projector-built documents load as documents. [ReadAggregate] would be wrong here —
        // it only applies to a single-stream aggregation, and a fan-out is not one.
        writer.Write($"BLOCK:public static class Get{readModel}Endpoint");
        writer.WriteLine($"[WolverineGet(\"/api/{readModel.ToLowerInvariant()}/{{id}}\")]");
        writer.WriteLine($"public static Task<{readModel}?> Get(Guid id, IQuerySession session, CancellationToken ct)");
        writer.WriteLine($"    => session.LoadAsync<{readModel}>(id, ct);");
        writer.FinishBlock();
        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }
}
