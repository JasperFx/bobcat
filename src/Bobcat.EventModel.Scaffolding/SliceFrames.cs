using Bobcat.EventModel.Emlang;
using JasperFx.CodeGeneration;

namespace Bobcat.EventModel.Scaffolding;

/// <summary>A positional record declaration, fields synthesized from the model's hints.</summary>
public class RecordFrame : ScaffoldFrame
{
    private readonly string _name;
    private readonly IReadOnlyList<(string Type, string Name)> _fields;
    private readonly string? _docComment;

    public RecordFrame(string name, IReadOnlyList<(string Type, string Name)> fields, string? docComment = null)
    {
        _name = name;
        _fields = fields;
        _docComment = docComment;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_docComment is not null)
        {
            writer.WriteLine($"/// <summary>{_docComment}</summary>");
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

    public WriteModelHandlerFrame(CuratedSlice slice, bool maybeNewStream)
    {
        _slice = slice;
        _maybeNewStream = maybeNewStream;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var aggregate = _slice.Aggregates.FirstOrDefault() ?? $"{_slice.Name}Model";
        var isAutomation = _slice.Pattern == "Automation";
        var trigger = isAutomation
            ? _slice.Trigger?.Label is { } label ? EmlangImport.PascalName(label) : _slice.Events.FirstOrDefault() ?? "TodoTriggerEvent"
            : _slice.Command ?? _slice.Name;

        writer.WriteLine("/// <summary>");
        writer.WriteLine(isAutomation
            ? $"/// Automation slice: triggered by the {trigger} event, never by a route. Decides and returns —"
            : "/// State-change slice: decides only \"is this request valid\" — every further consequence");
        writer.WriteLine(isAutomation
            ? "/// the framework loads the aggregate, appends, and commits. Design for at-least-once delivery."
            : "/// is a separate automation triggered by an event appended here.");
        writer.WriteLine("/// </summary>");
        writer.Write($"BLOCK:public static class {_slice.Name}Handler");

        var parameter = _maybeNewStream ? $"[WriteModel] {aggregate}? " : $"[WriteModel] {aggregate} ";
        var argument = char.ToLowerInvariant(aggregate[0]) + aggregate[1..];
        writer.Write(
            $"BLOCK:public static EventsToAppend Handle({trigger} {(isAutomation ? "trigger" : "command")}, {parameter}{argument})");

        foreach (var hotspot in _slice.Hotspots)
        {
            writer.WriteLine($"// HOTSPOT (from the model): {hotspot}");
        }

        foreach (var refusal in refusals())
        {
            writer.WriteLine(
                $"// TODO guard: throw new InvalidOperationException(\"{refusal}\"); (asserted by `validation fails with`)");
        }

        writer.WriteLine("// TODO: the decision. Nothing to append is `return [];` — never a nullable event (wolverine#4309).");
        var events = string.Join(", ", _slice.Events.Select(x => $"new {x}(/* TODO */)"));
        writer.WriteLine(events.Length > 0 ? $"return [{events}];" : "return [];");
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
/// The HTTP face of a state-change slice: a pure translation minting identity and cascading the
/// command through the transactional outbox — no mediator hop, nothing a crash can tear in half.
/// </summary>
public class EndpointTranslationFrame : ScaffoldFrame
{
    private readonly CuratedSlice _slice;
    private readonly string _route;

    public EndpointTranslationFrame(CuratedSlice slice, string route)
    {
        _slice = slice;
        _route = route;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var command = _slice.Command ?? _slice.Name;
        writer.Write($"BLOCK:public static class {_slice.Name}Endpoint");
        writer.WriteLine($"[WolverinePost(\"{_route}\")]");
        writer.Write($"BLOCK:public static (CreationResponse, {command}) Post({command}Request request)");
        writer.WriteLine("// TODO: mint identity here at the edge (Guid.NewGuid(), or the slice's deterministic id");
        writer.WriteLine("// helper), then cascade the command — the cascade rides the transactional outbox.");
        writer.WriteLine($"var command = new {command}(/* TODO from request */);");
        writer.WriteLine($"return (new CreationResponse(\"{_route}/\" + /* TODO: the minted id */ Guid.Empty), command);");
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

        writer.Write($"BLOCK:public class {readModel}");
        writer.WriteLine("public Guid Id { get; set; }");
        writer.WriteLine("// TODO: the projected columns the model's scenarios assert on");
        writer.FinishBlock();
        writer.BlankLine();

        if (_slice.Projections.Count > 0)
        {
            writer.WriteLine("// Async lifecycle: register with the daemon RUNNING (AddAsyncDaemon), or this never advances.");
            writer.Write($"BLOCK:public class {_slice.Projections[0]} : SingleStreamProjection<{readModel}, Guid>");
            writer.WriteLine("// TODO: Apply methods per source event");
            writer.FinishBlock();
            writer.BlankLine();
        }
        else
        {
            writer.WriteLine("// No projector declared: this read model is an entity's own Inline snapshot read back by id.");
        }

        writer.Write($"BLOCK:public static class Get{readModel}Endpoint");
        writer.WriteLine($"[WolverineGet(\"/api/{readModel.ToLowerInvariant()}/{{id}}\")]");
        writer.WriteLine($"public static Task<{readModel}?> Get(Guid id, IQuerySession session, CancellationToken ct)");
        writer.WriteLine($"    => session.LoadAsync<{readModel}>(id, ct);");
        writer.FinishBlock();
        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }
}
