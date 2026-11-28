using Bobcat.EventModel.Emlang;

namespace Bobcat.EventModel.Scenarios;

/// <summary>
/// One board example as a specification would state it (bobcat#846): what is given, the one act, and
/// the outcomes, each typed by what it is rather than by the element kind it names. Every reader
/// (<see cref="EmlangReader"/>, <see cref="EventModelersJsonReader"/>, the readers to come) produces
/// one per test; the import binds it to its slice (<see cref="CuratedScenario.Model"/>); Wolverine's
/// <c>write-specs</c> consumes it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EmlangTest"/> says which elements an example names and nothing more, so a writer reading
/// it had to hard-code <c>WhenReceived</c> and <c>ThenRefusedWith</c>. This record can say "this act is
/// an HTTP request", "this read model is a list and the rows are the whole answer", "this field is
/// generated, leave it out of the comparison", "this event carries these tags", "this is the read model
/// the command decided against".
/// </para>
/// <para>
/// Shaped so it projects onto JasperFx's <c>SpecificationDescriptor</c> body later (jasperfx#1300)
/// without a redesign: identity, slice binding, status and the given/act/outcome kinds are the same
/// either way. Values are text, as the board gives them; <c>CellValues</c> reads them when a
/// specification runs, so <c>NULL</c>, <c>EMPTY</c> and relative times mean what they mean everywhere.
/// </para>
/// </remarks>
public sealed record ScenarioModel(string Name)
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    // ---- binding and status -------------------------------------------------------------------

    /// <summary>The slice the import attached the example to; null on a reader's output, before the import binds it.</summary>
    public string? Slice { get; init; }

    /// <summary>The chapter the example was written under (the board's, where it states one).</summary>
    public string? Chapter { get; init; }

    /// <summary>The slice's pattern — Command, View, Automation, Translation — once bound.</summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// Normal, or what makes a generated specification pending: a board's <c>Blocked</c> slice is
    /// <see cref="ScenarioStatus.Pending"/>, an <c>Informational</c> one <see cref="ScenarioStatus.Informational"/>.
    /// </summary>
    public ScenarioStatus Status { get; init; } = ScenarioStatus.Normal;

    /// <summary>The rule the example illustrates, when the source names one (a dcb.events rule, an ESDM invariant). Ignored by the writer until bobcat#575.</summary>
    public string? Rule { get; init; }

    /// <summary>Background the source could not say with elements (an EM Spec <c>contextNote</c>); carried as a comment, never as a step.</summary>
    public string? Note { get; init; }

    /// <summary>The source's own id for the example (an EM Spec specification's <c>id</c>), for a tool that wants to join back; null when the source has none.</summary>
    public string? SourceId { get; init; }

    // ---- givens ---------------------------------------------------------------------------------

    /// <summary>Prior events, oldest first.</summary>
    public IReadOnlyList<GivenEvent> GivenEvents { get; init; } = [];

    /// <summary>The read models the act decides against (an EM Spec <c>context</c>; an emlang <c>v:</c> in a <c>given</c>).</summary>
    public IReadOnlyList<ContextReadModel> Context { get; init; } = [];

    /// <summary>The clock the act runs under, as the source wrote it, when it says.</summary>
    public string? Clock { get; init; }

    /// <summary>The tenant, when the source says.</summary>
    public string? Tenant { get; init; }

    /// <summary>Who acts: the slice's one actor, when the board names exactly one.</summary>
    public string? Actor { get; init; }

    // ---- the act --------------------------------------------------------------------------------

    /// <summary>The one act, or null for an example that only asserts how events project (a view's given/then).</summary>
    public ScenarioAct? Act { get; init; }

    // ---- outcomes -------------------------------------------------------------------------------

    public IReadOnlyList<ScenarioOutcome> Outcomes { get; init; } = [];

    /// <summary>Whether the example expects a refusal.</summary>
    public bool Refuses => Outcomes.Any(x => x is Refusal);

    /// <summary>
    /// The model <see cref="EmlangReader"/> lowers a <c>tests:</c> entry to, and what every reader's
    /// output means when it says no more than emlang can: an event given is an event on no stated
    /// stream, a view given is context, the <c>when</c> command is a message received, a <c>then</c>
    /// event is emitted, a <c>then</c> view is a partial read model (a whole one when it names no
    /// value), and an <c>x:</c> is a refusal whose values are what its message names.
    /// </summary>
    public static ScenarioModel From(EmlangTest test)
    {
        var command = test.When.FirstOrDefault(x => x.Kind == EmlangElementKind.Command);

        return new ScenarioModel(test.Name)
        {
            GivenEvents = test.Given.Where(x => x.Kind == EmlangElementKind.Event)
                .Select(x => new GivenEvent(x.Label, x.Props)).ToList(),
            Context = test.Given.Where(x => x.Kind == EmlangElementKind.View)
                .Select(x => new ContextReadModel(x.Label, x.Props)).ToList(),
            Act = command is null ? null : new MessageReceived(command.Label, command.Props),
            Outcomes = test.Then.Select(outcome).OfType<ScenarioOutcome>().ToList(),
        };
    }

    private static ScenarioOutcome? outcome(EmlangRef reference) => reference.Kind switch
    {
        EmlangElementKind.Event => new EventEmitted(reference.Label, reference.Props),
        EmlangElementKind.View => new ReadModelOutcome(reference.Label,
            reference.Props.Count > 0 ? ReadModelShape.Partial : ReadModelShape.Single, MatchMode.Subset, reference.Props),
        EmlangElementKind.Error => new Refusal(reference.Label, RefusalKind.Refused, reference.Props.Values.Where(x => x.Length > 0).ToList()),
        _ => null,
    };

    /// <summary>
    /// The same example as the element-naming shape (<see cref="EmlangTest"/>), for the writer that
    /// still reads that: a context read model is a view given, an HTTP request or a message is its
    /// command, a refusal is an <c>x:</c>. Lossy by design; the model is the record.
    /// </summary>
    public EmlangTest ToEmlangTest()
    {
        var given = GivenEvents.Select(x => new EmlangRef(EmlangElementKind.Event, x.Stream ?? "", x.Event) { Props = x.Values, Values = raw(x.Values) })
            .Concat(Context.Select(x => new EmlangRef(EmlangElementKind.View, "", x.ReadModel) { Props = x.Values, Values = raw(x.Values) }))
            .ToList();

        var when = Act switch
        {
            MessageReceived m => [new EmlangRef(EmlangElementKind.Command, "", m.Message) { Props = m.Values, Values = raw(m.Values) }],
            MessagePublished m => [new EmlangRef(EmlangElementKind.Command, "", m.Message) { Props = m.Values, Values = raw(m.Values) }],
            HttpRequest h when h.Body is not null => [new EmlangRef(EmlangElementKind.Command, "", h.Body) { Props = h.Values, Values = raw(h.Values) }],
            _ => new List<EmlangRef>(),
        };

        var then = new List<EmlangRef>();
        foreach (var o in Outcomes)
        {
            switch (o)
            {
                case EventEmitted e:
                    then.Add(new EmlangRef(EmlangElementKind.Event, e.Stream ?? "", e.Event) { Props = e.Values, Values = raw(e.Values) });
                    break;
                case ReadModelOutcome r:
                    then.Add(new EmlangRef(EmlangElementKind.View, "", r.ReadModel) { Props = r.Values, Values = raw(r.Values) });
                    break;
                case Refusal f:
                    var mentioned = f.Mentioned.Select((v, i) => KeyValuePair.Create($"value{i + 1}", v)).ToDictionary(StringComparer.Ordinal);
                    then.Add(new EmlangRef(EmlangElementKind.Error, "", f.Reason) { Props = mentioned, Values = raw(mentioned) });
                    break;
            }
        }

        return new EmlangTest(Name, given, when, then) { Model = this };
    }

    private static IReadOnlyDictionary<string, object?> raw(IReadOnlyDictionary<string, string> values)
        => values.ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, string> None => NoValues;
}

public enum ScenarioStatus
{
    Normal,

    /// <summary>The slice is not ready to be specified (a board's <c>Blocked</c>): the specification is written skipped.</summary>
    Pending,

    /// <summary>The slice documents rather than specifies (a board's <c>Informational</c>): written skipped, said why.</summary>
    Informational,
}

/// <summary>A prior event: its type, the values the example gives it, the stream it is on when the model says, and its DCB tags.</summary>
public sealed record GivenEvent(string Event, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>The aggregate whose stream the event is on, when the model declares it; null leaves it to the slice's.</summary>
    public string? Stream { get; init; }

    /// <summary>Tag name → value, for the fields the model marks as tags (<c>tag: true</c>); empty for an untagged event.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = ScenarioModel.None;
}

/// <summary>A read model the act decides against, stored as it is: one document's values, or the rows of a list.</summary>
public sealed record ContextReadModel(string ReadModel, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>The rows, for a list read model; null for a single document.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>>? Rows { get; init; }
}

// ---- acts -----------------------------------------------------------------------------------

/// <summary>The one thing an example does. Closed: a writer switches over the kinds and a kind it has no words for is a reported gap, never a guess.</summary>
public abstract record ScenarioAct;

/// <summary>A message (a command) handled: <c>WhenReceived</c>.</summary>
public sealed record MessageReceived(string Message, IReadOnlyDictionary<string, string> Values) : ScenarioAct;

/// <summary>A message published to the bus: <c>WhenPublished</c>.</summary>
public sealed record MessagePublished(string Message, IReadOnlyDictionary<string, string> Values) : ScenarioAct;

/// <summary>An HTTP request: the verb, the route, the body type (the command, when the route takes one) with its values, and route values.</summary>
public sealed record HttpRequest(string Method, string Route, string? Body, IReadOnlyDictionary<string, string> Values) : ScenarioAct
{
    public IReadOnlyDictionary<string, string> RouteValues { get; init; } = ScenarioModel.None;
}

/// <summary>A read model queried with parameters (an EM Spec inline <c>QUERY</c>): the outcome depends on how the view is asked.</summary>
public sealed record ReadModelQueried(string ReadModel, IReadOnlyDictionary<string, string> Parameters) : ScenarioAct;

/// <summary>Time advancing, as the source wrote it (<c>WhenTimePasses</c>).</summary>
public sealed record TimeAdvances(string By) : ScenarioAct;

/// <summary>An event from another system entering a translation slice.</summary>
public sealed record ForeignEventReceived(string Event, IReadOnlyDictionary<string, string> Values) : ScenarioAct;

/// <summary>The scheduled messages executing (<c>WhenScheduledMessagesAreExecuted</c>).</summary>
public sealed record ScheduledMessagesExecute : ScenarioAct;

// ---- outcomes -------------------------------------------------------------------------------

public abstract record ScenarioOutcome;

/// <summary>An event appended: its values, the stream when the model says, its tags, and the members a comparison leaves out (the model's <c>generated</c> fields).</summary>
public sealed record EventEmitted(string Event, IReadOnlyDictionary<string, string> Values) : ScenarioOutcome
{
    public string? Stream { get; init; }
    public IReadOnlyDictionary<string, string> Tags { get; init; } = ScenarioModel.None;
    public IReadOnlyList<string> IgnoredMembers { get; init; } = [];
}

/// <summary>What shape the read model assertion takes.</summary>
public enum ReadModelShape
{
    /// <summary>The one document exists; nothing about its values.</summary>
    Single,

    /// <summary>The one document, with the members the example names.</summary>
    Partial,

    /// <summary>A list, with the rows the example names.</summary>
    Rows,

    /// <summary>No document, or an empty list (<c>expectEmptyList</c>).</summary>
    None,
}

/// <summary>How rows or members are matched: exactly (the whole answer) or as a subset (these among others).</summary>
public enum MatchMode
{
    Exact,
    Subset,
}

/// <summary>
/// The read model after the act. <b>Rows default to the exact set in any order</b> (decided 2026-11-27):
/// a board's list rows are the whole answer, and an empty list is "no items", not "nothing asserted".
/// A single document's named members are a subset: the example names what it is about. A source with
/// its own match mode (prooph's <c>match</c>) is honoured when present.
/// </summary>
public sealed record ReadModelOutcome(string ReadModel, ReadModelShape Shape, MatchMode Match, IReadOnlyDictionary<string, string> Values) : ScenarioOutcome
{
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; init; } = [];

    /// <summary>The document's id, when the example names one apart from its values.</summary>
    public string? Id { get; init; }
}

/// <summary>Why a refusal was refused, as far as the source says.</summary>
public enum RefusalKind
{
    /// <summary>Input the business refuses outright.</summary>
    Validation,

    /// <summary>A rule or state refusal: what an <c>x:</c>, <c>expectError</c> or <c>then rejected</c> says.</summary>
    Refused,

    /// <summary>An HTTP refusal answered as problem details.</summary>
    ProblemDetails,

    /// <summary>The stream or document does not exist.</summary>
    NotFound,
}

/// <summary>The act was refused: the reason in the source's words, what kind, and the values the reason mentions.</summary>
public sealed record Refusal(string Reason, RefusalKind Kind, IReadOnlyList<string> Mentioned) : ScenarioOutcome;

/// <summary>A message the act caused, sent now or scheduled.</summary>
public sealed record MessageCaused(string Message, IReadOnlyDictionary<string, string> Values, bool Scheduled) : ScenarioOutcome;

/// <summary>For a DCB slice: the boundary query the act is expected to read, and the events that would or would not conflict with it (filled by the dcb.events and ESDM readers, bobcat#849/#850).</summary>
public sealed record BoundaryExpectation(string Query, IReadOnlyList<string> Conflicting, IReadOnlyList<string> NotConflicting) : ScenarioOutcome;
