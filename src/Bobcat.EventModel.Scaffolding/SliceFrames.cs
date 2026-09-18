using JasperFx.CodeGeneration;

namespace Bobcat.EventModel.Scaffolding;

/// <summary>A positional record declaration, fields synthesized from the model's hints.</summary>
/// <summary>
/// Declares the marker interface a multi-stream view routes by (issue #347).
/// </summary>
/// <remarks>
/// It lives in the VIEW's file, not with the events: the interface answers the view's routing
/// question, and the events it marks are spread across the slices that emit them — there is no one
/// event file it would belong in. Where two views ask the same question they share one interface,
/// declared by whichever view sorts first, so the output does not move when a slice is renamed.
/// </remarks>
public class MarkerInterfaceFrame : ScaffoldFrame
{
    private readonly MarkerView _marker;

    public MarkerInterfaceFrame(MarkerView marker) => _marker = marker;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("/// <summary>");
        writer.WriteLine($"/// Routes by {_marker.Marker.Field}: {string.Join(", ", _marker.Events)}.");
        writer.WriteLine("/// </summary>");
        writer.Write($"BLOCK:public interface {_marker.Marker.Name}");
        writer.WriteLine($"Guid {_marker.Marker.Field} {{ get; }}");
        writer.FinishBlock();
        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// The block comment at the top of a chapter's <c>Events.cs</c>, saying that this is all of them.
/// </summary>
/// <remarks>
/// The list is the point. A reader who wants to know what a chapter can emit should not have to
/// trust that the file is complete, and a name in this header with no record under it — or a record
/// with no name in the header — is a regeneration that went wrong, visible without a diff.
/// </remarks>
public class ChapterEventsHeaderFrame : ScaffoldFrame
{
    private readonly string _chapter;
    private readonly IReadOnlyList<string> _events;

    public ChapterEventsHeaderFrame(string chapter, IReadOnlyList<string> events)
    {
        _chapter = chapter;
        _events = events;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"// Every event the {_chapter} chapter emits — all {_events.Count} of them, and nothing else.");
        writer.WriteLine("//");
        writer.WriteLine("// Gathered here rather than beside the commands that append them, because \"what can happen");
        writer.WriteLine("// in this chapter\" is a question about the chapter. A slice file holds its own slice.");
        writer.WriteLine("//");
        writer.WriteLine("// Events that arrive from OUTSIDE this model are not here — they keep their own files, and");
        writer.WriteLine("// each says to version rather than edit it.");
        writer.WriteLine("//");

        foreach (var @event in _events)
        {
            writer.WriteLine($"//   {@event}");
        }

        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }
}

public class RecordFrame : ScaffoldFrame
{
    private readonly string _name;
    private readonly IReadOnlyList<(string Type, string Name)> _fields;
    private readonly string? _docComment;
    private readonly IReadOnlyList<string> _warnings;
    private readonly IReadOnlyList<string> _interfaces;

    public RecordFrame(string name, IReadOnlyList<(string Type, string Name)> fields, string? docComment = null,
        IReadOnlyList<string>? warnings = null, IReadOnlyList<string>? interfaces = null)
    {
        _name = name;
        _fields = fields;
        _docComment = docComment;
        _warnings = warnings ?? [];
        _interfaces = interfaces ?? [];
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
        // The marker interfaces a multi-stream view routes by (issue #347). An event may carry
        // several: AppointmentsQueue keys by shelter and MyAppointments by owner, over the same
        // events, so one record implements both.
        var markers = _interfaces.Count == 0 ? "" : $" : {string.Join(", ", _interfaces)}";
        writer.WriteLine($"public record {_name}({fields}){markers};");
        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// The self-aggregating write model: Create for the first event, Apply per event — the only
/// mutators, owned by the store.
/// </summary>
/// <summary>
/// How a scaffolded auto-property is written, so a <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c> project — the
/// <c>dotnet new</c> default — builds without CS8618 (issue #242).
/// </summary>
/// <remarks>
/// Small, but the same principle as #226: the scaffold's job is to hand back something that builds
/// cleanly, so the only thing left to do is the decision. A repo with TreatWarningsAsErrors
/// otherwise gets a red build out of a scaffold that is supposed to be green, and the warning is
/// not one the reader can act on — it is asking them to initialize a property the projection or the
/// Create method is about to fill.
///
/// <c>null!</c> rather than a nullable declaration on purpose: making the property nullable would
/// push the warning into every consumer that reads it, and it would make the model lie — these are
/// values the fill-in assigns before anything reads them, not values that may be absent.
/// </remarks>
internal static class ScaffoldedProperty
{
    public static string Declare(string type, string name)
    {
        var initializer = type switch
        {
            "string" => " = string.Empty;",
            _ when isValueType(type) => "",
            _ => " = null!;"
        };

        return $"public {type} {name} {{ get; set; }}{initializer}";
    }

    private static bool isValueType(string type)
        => type.EndsWith('?')
           || type is "Guid" or "int" or "long" or "short" or "byte" or "bool" or "decimal"
               or "double" or "float" or "DateTimeOffset" or "DateTime" or "DateOnly" or "TimeOnly"
               or "TimeSpan";
}

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
            writer.WriteLine(ScaffoldedProperty.Declare(type, fieldName));
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
    private readonly bool _startsStream;
    private readonly IReadOnlyList<CascadedMessage> _cascaded;
    private readonly IReadOnlyList<string> _warnings;
    private readonly string? _publishedBy;

    public WriteModelHandlerFrame(CuratedSlice slice, bool maybeNewStream, bool startsStream = false,
        IReadOnlyList<CascadedMessage>? cascaded = null, IReadOnlyList<string>? warnings = null,
        string? publishedBy = null)
    {
        _slice = slice;
        _maybeNewStream = maybeNewStream;
        _startsStream = startsStream;
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

        // A slice whose act cannot identify a stream and whose scenarios arrange none STARTS the
        // stream, and a [WriteModel] cannot start one — it loads an existing stream (issue #239).
        // The store-agnostic side effect is the shape, and there is no aggregate to bind at all.
        var creates = _startsStream;
        var parameter = _maybeNewStream ? $"[WriteModel] {aggregate}? " : $"[WriteModel] {aggregate} ";
        var argument = char.ToLowerInvariant(aggregate[0]) + aggregate[1..];
        var appendType = creates ? "StartStream" : "EventsToAppend";
        var returnType = _cascaded.Count == 0
            ? appendType
            : $"({appendType}, {string.Join(", ", _cascaded.Select(x => x.Name))})";
        var arguments = $"{trigger} {(isAutomation ? "trigger" : "command")}"
                        + (creates ? "" : $", {parameter}{argument}");
        writer.Write($"BLOCK:public static {returnType} Handle({arguments})");

        foreach (var hotspot in _slice.Hotspots)
        {
            writer.WriteLine($"// HOTSPOT (from the model): {hotspot}");
        }

        foreach (var warning in _warnings)
        {
            writer.WriteLine($"// WARNING (from the model): {warning}");
        }

        foreach (var refusal in SliceScaffolder.RefusalsOf(_slice))
        {
            writer.WriteLine(
                $"// TODO guard: throw new InvalidOperationException(\"{refusal.Reason}\"); (asserted by `validation fails with`)");
        }

        writer.WriteLine(creates
            ? "// The decision. Every scenario of this slice arranges no prior events, so it starts the"
            : "// The decision. Nothing to append is `return [];` — never a nullable event (wolverine#4309).");
        if (creates)
        {
            writer.WriteLine($"// stream: mint the id (or take it off the trigger) and hand back the {aggregate}'s first event.");
        }

        foreach (var message in _cascaded)
        {
            writer.WriteLine(message.LeavesTheSystem
                ? $"// {message.Name} leaves the system (outbound external edge); the cascade rides the transactional outbox."
                : $"// The model designates {message.Name} as bus-visible — slice '{message.HandledBy}' handles it; the cascade rides the transactional outbox.");
        }

        var events = string.Join(", ", _slice.Events.Select(x => $"new {x}(/* … */)"));
        var appended = events.Length > 0 ? $"[{events}]" : "[]";
        var cascades = string.Join(", ", _cascaded.Select(x => $"new {x.Name}(/* … */)"));

        if (creates)
        {
            var started = $"Storage.StartStream<{aggregate}>(id{(events.Length > 0 ? ", " + events : "")})";
            writeUnfilledDecision(writer,
                $"{_slice.Name} — decide which event starts the stream, and what its id is",
                "var id = Guid.NewGuid();   // or the identity the trigger already carries",
                _cascaded.Count == 0 ? $"return {started};" : $"return ({started}, {cascades});");
        }
        else
        {
            writeUnfilledDecision(writer,
                $"{_slice.Name} — decide which events this slice appends",
                _cascaded.Count == 0 ? $"return {appended};" : $"return ({appended}, {cascades});");
        }
        writer.FinishBlock();
        writer.FinishBlock();
        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }

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
    private readonly bool _startsStream;

    public CollapsedEndpointFrame(CuratedSlice slice, string route,
        IReadOnlyList<CascadedMessage>? cascaded = null, IReadOnlyList<string>? warnings = null,
        bool startsStream = false)
    {
        _slice = slice;
        _route = route;
        _cascaded = cascaded ?? [];
        _warnings = warnings ?? [];
        _startsStream = startsStream;
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

        // A refusal the model states over arranged history is about the aggregate's STATE, and
        // `request` cannot answer that (issue #238) — so the guard binds the aggregate too.
        //
        // NO attribute on it (issue #345). The [WriteModel] this frame emits below is the load:
        // codegen-preview shows one FetchForWriting and `Validate(stream_x.Aggregate)` with or
        // without a [ReadModel] here, so the attribute is not a second load — it is a second
        // CONCEPT where there is only one, leaving a reader to work out which model the guard ran
        // against. Requiredness still reads off `{optional}`, which is the same on both signatures.
        var onState = SliceScaffolder.RefusesOnState(_slice);

        // The model declaring a 404 over an unarranged stream says the write model is REQUIRED
        // (issue #337): Wolverine's own not-found guard answers it, before Validate runs. Binding
        // it nullable and scaffolding a TODO would invite the unreachable `if (x is null)` that
        // CritterCrush carried eleven copies of.
        // A slice that STARTS its stream cannot also refuse because the stream is missing. The
        // model saying both is a contradiction, and the creating path is the one that must keep
        // working — so the parameter stays nullable and the scaffold says why out loud.
        var contradiction = SliceScaffolder.RefusesMissingStream(_slice) && _startsStream;
        var required = SliceScaffolder.RefusesMissingStream(_slice) && !_startsStream;
        var optional = required ? "" : "?";

        writer.Write(onState
            ? $"BLOCK:public static ProblemDetails Validate({command} command, {aggregate}{optional} {argument})"
            : $"BLOCK:public static ProblemDetails Validate({command} command)");

        var refusals = SliceScaffolder.RefusalsOf(_slice);
        if (onState)
        {
            writer.WriteLine($"// The model's refusing scenarios arrange prior events, so these refusals are about");
            writer.WriteLine(required
                ? $"// {argument}'s state, not the request's shape. It is never null — see the 404 below."
                : $"// {argument}'s state, not the request's shape. Null means the stream does not exist yet.");
        }

        foreach (var refusal in refusals)
        {
            if (refusal.FromTheFramework)
            {
                writer.WriteLine(
                    $"// 404 (\"{refusal.Reason}\") is Wolverine's own guard on the required {aggregate} below:");
                writer.WriteLine(
                    "// it answers before this method runs, so there is no guard to write here. A null");
                writer.WriteLine(
                    $"// check on {argument} would be unreachable code that looks load-bearing.");
                continue;
            }

            writer.WriteLine(
                $"// TODO guard: return new ProblemDetails {{ Detail = \"{refusal.Reason}\", Status = {refusal.HttpStatus} }};");
        }

        writer.WriteLine("return WolverineContinue.NoProblems;");
        writer.FinishBlock();
        writer.BlankLine();

        writer.WriteLine($"[WolverinePost(\"{_route}\")]");

        // [EmptyResponse], not an empty response record (issue #346). The record this used to
        // declare carried nothing and could carry nothing — the scaffolder had no shape to put in
        // it — so it bought a type name that names nothing, a tuple return, and a 200 where the
        // honest answer is 204. A slice that really does answer with data says so by hand; that is
        // one edit against thirteen deletions.
        writer.WriteLine("[EmptyResponse]");
        var returnType = _cascaded.Count == 0
            ? "EventsToAppend"
            : $"(EventsToAppend{string.Concat(_cascaded.Select(x => $", {x.Name}"))})";
        writer.Write(
            $"BLOCK:public static {returnType} Post({command} command, [WriteModel] {aggregate}{optional} {argument})");

        foreach (var hotspot in _slice.Hotspots)
        {
            writer.WriteLine($"// HOTSPOT (from the model): {hotspot}");
        }

        foreach (var warning in _warnings)
        {
            writer.WriteLine($"// WARNING (from the model): {warning}");
        }

        if (contradiction)
        {
            writer.WriteLine("// WARNING (from the model): a scenario refuses with 404 for a stream that does not");
            writer.WriteLine($"// exist, but every scenario here arranges none, so this slice STARTS the {aggregate}'s");
            writer.WriteLine("// stream. Both cannot be true; the parameter is left nullable. Fix the model.");
        }

        writer.WriteLine(_cascaded.Count == 0
            ? "// The decision. Nothing to append is `return [];` — never a nullable event (wolverine#4309)."
            : "// The decision. Nothing to append is `return ([], ...);` — never a nullable event (wolverine#4309).");
        writer.WriteLine("// A computed stream id belongs on the request record: [Identity] public Guid ...Id => ...;");
        foreach (var message in _cascaded)
        {
            writer.WriteLine(message.LeavesTheSystem
                ? $"// {message.Name} leaves the system (outbound external edge); the cascade rides the transactional outbox."
                : $"// The model designates {message.Name} as bus-visible — slice '{message.HandledBy}' handles it; the cascade rides the transactional outbox.");
        }

        writer.WriteLine("// Answering with a body instead of 204: drop [EmptyResponse], declare the response");
        writer.WriteLine("// record, and return it beside the events as a tuple.");
        var events = string.Join(", ", _slice.Events.Select(x => $"new {x}(/* … */)"));
        var cascades = string.Concat(_cascaded.Select(x => $", new {x.Name}(/* … */)"));
        writeUnfilledDecision(writer,
            $"{_slice.Name} — decide which events this slice appends",
            _cascaded.Count == 0
                ? $"return [{events}];"
                : $"return ([{events}]{cascades});");
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
        // The incoming type keeps the board's name too, so the pair reads as two commands rather
        // than as a request that turns into one.
        var incoming = char.ToLowerInvariant(request[0]) + request[1..];
        writer.Write($"BLOCK:public static (CreationResponse, {command}) Post({request} {incoming})");
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

/// <summary>
/// A read-model + projection + GET endpoint skeleton for a View slice — and a projection that
/// <em>registers</em>, which is a higher bar than one that compiles (issue #232).
/// </summary>
/// <remarks>
/// The empty projection this used to emit compiled perfectly and could not be registered: Marten
/// validates that a projection has at least one conventional method, so <c>Program.cs</c> threw
/// "No matching conventional Apply/Create/ShouldDelete methods", the host resource never started,
/// and all eleven scenarios of the chapter reported <c>did not run</c>. Two unfilled View slices
/// took down nine other slices' specs — the runtime form of exactly the "one hole fails
/// everything" property #226 removed from the compile side.
///
/// So "the scaffold compiles" is the wrong bar; "the host boots" is the bar. Measured against a
/// real Marten (see <c>ProjectionRegistrationTests</c>): a conventional <c>Apply</c> is enough for
/// a single-stream projection, and a fan-out needs an <c>Identity</c> slicing rule as well or it
/// trades one registration failure for another ("is a multi-stream projection, but has no defined
/// event slicing rules").
/// </remarks>
public class ViewSliceFrame : ScaffoldFrame
{
    private readonly CuratedSlice _slice;
    private readonly IReadOnlyList<ViewSource> _sources;
    private readonly IReadOnlyList<(string Type, string Name)> _fields;

    public ViewSliceFrame(CuratedSlice slice, IReadOnlyList<ViewSource>? sources = null,
        IReadOnlyList<(string Type, string Name)>? fields = null)
    {
        _slice = slice;
        _sources = sources ?? [];
        _fields = fields ?? [];
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

        // The projected columns, from the same two sources every other frame reads: the slice's
        // `elements:` hints and the columns its scenarios assert on (issue #240). Both were
        // sitting there unused, and the read model was the one type in the scaffold that came out
        // empty however well the model was curated.
        writer.Write($"BLOCK:public class {readModel}");
        writer.WriteLine("public Guid Id { get; set; }");
        if (_fields.Count == 0)
        {
            writer.WriteLine("// TODO: the projected columns — this model's `elements:` and scenarios name none.");
        }

        foreach (var (type, name) in _fields.Where(x => x.Name != "Id"))
        {
            writer.WriteLine(ScaffoldedProperty.Declare(type, name));
        }

        writer.FinishBlock();
        writer.BlankLine();

        writeProjection(writer, readModel);

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

    private void writeProjection(ISourceWriter writer, string readModel)
    {
        var projection = _slice.Projections[0];

        if (_sources.Count == 0)
        {
            // Nothing in the model to fold. A projection class with no conventional method cannot
            // be registered at all, so emitting one here would hand over a host that will not
            // boot — the defect this type exists to avoid.
            writer.WriteLine($"// No {projection} yet: this model names no event for {readModel} to fold, and a");
            writer.WriteLine("// projection with no Apply method cannot be registered — it would stop the host booting.");
            writer.WriteLine("// Add the source events to the model and regenerate.");
            writer.BlankLine();
            return;
        }

        var routable = _sources.Where(x => x.IdentityField is not null).ToList();
        var fanOut = _slice.FanOut && routable.Count > 0;
        var sources = fanOut ? routable : _sources;

        writer.WriteLine("// Async lifecycle: register with the daemon RUNNING (AddAsyncDaemon), or this never advances.");

        if (_slice.FanOut && !fanOut)
        {
            writer.WriteLine("// The model asks for a fan-out, but names no Guid field on any source event to slice");
            writer.WriteLine("// by — and a multi-stream projection with no slicing rule cannot be registered at all.");
            writer.WriteLine("// Single-stream until the model names one; then swap the base class back and add the");
            writer.WriteLine("// Identity<T>(x => x.SomeId) / Identities<T>(x => [x.OneId, x.OtherId]) routing.");
        }

        // Every routed event keyed the same way is one rule, not N (issue #347): a marker interface
        // carries the identity, so the constructor states the rule once and the fold is a single
        // Evolve. Deriving the marker needs every source to agree on the field — two keys cannot be
        // one interface — so a mixed view keeps the per-event shape, which is the honest answer
        // rather than a half-applied one.
        var marker = fanOut ? MarkerInterface.For(sources) : null;

        if (fanOut)
        {
            writer.Write($"BLOCK:public class {projection} : MultiStreamProjection<{readModel}, Guid>");
            writer.Write($"BLOCK:public {projection}()");
            writer.WriteLine("// The slicing rule, without which this projection cannot be registered. One document");
            writer.WriteLine("// per key; Identities<T>(x => [x.OneId, x.OtherId]) where one event updates several.");
            if (marker is not null)
            {
                writer.WriteLine($"Identity<{marker.Name}>(x => x.{marker.Field});");
            }
            else
            {
                foreach (var source in sources)
                {
                    writer.WriteLine($"Identity<{source.Event}>(x => x.{source.IdentityField});");
                }
            }

            writer.FinishBlock();

            if (_sources.Count > routable.Count)
            {
                var dropped = _sources.Except(routable).Select(x => x.Event);
                writer.BlankLine();
                writer.WriteLine($"// Not sliced here: {string.Join(", ", dropped)}. The model names no Guid field on");
                writer.WriteLine("// them, so a routing rule would not compile. Add their identifiers to the model, or");
                writer.WriteLine("// write the Identity/Identities rules and Apply methods by hand.");
            }
        }
        else
        {
            writer.Write($"BLOCK:public class {projection} : SingleStreamProjection<{readModel}, Guid>");
        }

        if (marker is not null)
        {
            writer.BlankLine();
            // The BASE CLASS's virtual, taking IEvent — not a static convention method. A projection
            // whose only fold is a static `Evolve(view, IMarker)` does not register: JasperFx
            // reports "No matching conventional Apply/Create/ShouldDelete methods" at startup, so
            // the host does not boot and every scenario in the suite dies before a step runs. That
            // is issue #232's failure, and 0.26.1 shipped it (#351).
            // `{readModel}?` on the PARAMETER, because the base declares
            // `TDoc? Evolve(TDoc? snapshot, TId id, IEvent e)` — a non-nullable one is CS8765, and
            // the `??=` on the next line already admits it can be null. The RETURN stays
            // non-nullable: narrowing a nullable return is safe, and by then it never is.
            writer.Write($"BLOCK:public override {readModel} Evolve({readModel}? snapshot, Guid id, IEvent e)");
            writer.WriteLine($"snapshot ??= new {readModel} {{ Id = id }};");
            writer.BlankLine();
            writer.WriteLine($"// ONE place for anything derived from the identity — as Apply methods this was a line");
            writer.WriteLine("// at the top of every one of them, which is exactly where it goes missing.");
            writer.WriteLine($"if (e.Data is {marker.Name} routed) snapshot.{marker.Field} = routed.{marker.Field};");
            writer.BlankLine();
            writer.WriteLine("// Fill each arm in and delete the throw — the model's scenarios say what the view holds.");
            writer.Write("BLOCK:switch (e.Data)");
            foreach (var source in sources)
            {
                writer.WriteLine($"case {source.Event}:");
                writer.WriteLine($"    throw new NotImplementedException(\"TODO: {readModel} — project {source.Event}\");");
            }

            writer.FinishBlock();
            writer.BlankLine();
            writer.WriteLine("return snapshot;");
            writer.FinishBlock();
        }
        else
        {
            foreach (var source in sources)
            {
                var argument = char.ToLowerInvariant(source.Event[0]) + source.Event[1..];
                writer.BlankLine();
                writer.Write($"BLOCK:public void Apply({source.Event} {argument}, {readModel} view)");
                writer.WriteLine("// Fill this in and delete the throw — the model's scenarios say what the view holds.");
                writer.WriteLine("// Until then the projection stops on this event, so a scenario asserting the read model");
                writer.WriteLine("// fails on its projection wait rather than on a value.");
                writer.WriteLine($"throw new NotImplementedException(\"TODO: {readModel} — project {source.Event}\");");
                writer.FinishBlock();
            }
        }

        writer.FinishBlock();
        writer.BlankLine();
    }
}
