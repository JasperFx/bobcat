namespace Bobcat.EventModel;

/// <summary>
/// The curated event-model file (issue #201) — the serialized twin of a JasperFx
/// <c>EventModelDescriptor</c> plus scenario bodies. This is the format a human authors (or
/// reviews after an emlang import), and what <see cref="FileEventModelSource"/> loads onto the
/// Declared rung. Deliberately roles-only: it never carries elements or edges (those are
/// computed upstream on every read), and it never carries status or lifecycle — status is
/// derived from drift, not asserted here.
/// </summary>
public sealed class CuratedModelFile
{
    /// <summary>Format version. Only <c>1</c> is understood today; required so a future shape can be told apart.</summary>
    public int Schema { get; set; }

    /// <summary>
    /// Name of the Event Model this file contributes to — the merge key
    /// <c>EventModelDiscovery.Assemble</c> folds descriptors by. Must match the name the code-derived
    /// sources use (<c>opts.ServiceName</c> / <c>[assembly: EventModelName]</c>) or this file's model
    /// floats off as a second diagram instead of merging into the real one.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Optional root namespace for synthesizing the <c>FullName</c> of declared type references
    /// (<c>{namespace}.{Name}</c>). Declared types do not exist yet, so these are name-only
    /// placeholders that drift matching joins to the real CLR types once code is generated.
    /// </summary>
    public string? Namespace { get; set; }

    public List<CuratedSlice> Slices { get; set; } = [];
}

/// <summary>One slice's declared roles. The name is the merge key and by convention the command's short name.</summary>
public sealed class CuratedSlice
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Command | View | Automation | Translation. The board knows the pattern Gherkin cannot express.</summary>
    public string? Pattern { get; set; }

    public string? Domain { get; set; }

    /// <summary>
    /// The chapter — the span of the timeline this slice belongs to (issue #298, jasperfx#824).
    /// The emlang import carries the board's chapter name here; a curated file may say it outright.
    /// Independent of <see cref="Domain"/>: a bounded context has many chapters.
    /// </summary>
    public string? Chapter { get; set; }

    public CuratedTrigger? Trigger { get; set; }

    /// <summary>Bare type name of the inbound command, when the slice has one.</summary>
    public string? Command { get; set; }

    /// <summary>Bare type name of the handler / endpoint, when declared.</summary>
    public string? Handler { get; set; }

    public List<string> Aggregates { get; set; } = [];
    public List<string> Events { get; set; } = [];

    /// <summary>Published non-event messages — cascaded commands, integration messages.</summary>
    public List<string> Messages { get; set; } = [];

    public List<string> Projections { get; set; } = [];

    /// <summary>
    /// View slices only: true when one event updates MANY read-model documents (a multi-stream
    /// fan-out — e.g. a match updating both dogs' lists) rather than folding one stream into one
    /// document. A scaffolding hint the board cannot express and code cannot yet derive; the
    /// scaffolder emits a MultiStreamProjection with an Identities routing TODO when set.
    /// </summary>
    public bool FanOut { get; set; }

    public List<string> ReadModels { get; set; } = [];

    /// <summary>
    /// The events this slice <em>applies</em> — a View slice's projection inputs (issue #297,
    /// jasperfx#824). Distinct from <see cref="Events"/>, which the slice emits: a consumed event
    /// is drawn where it is consumed and linked back to the slice that emitted it, never as an
    /// output. The emlang import fills it from the <c>e:</c> steps preceding a <c>v:</c>.
    /// </summary>
    public List<string> ConsumedEvents { get; set; } = [];

    /// <summary>
    /// Read models this slice reads <em>before deciding</em> — the Automation pattern's input
    /// (issue #297, jasperfx#824). <see cref="ReadModels"/> stays what the slice produces.
    /// </summary>
    public List<string> ReadsFrom { get; set; } = [];

    public List<CuratedExternalSystem> ExternalSystems { get; set; } = [];

    /// <summary>Prose hotspots — open questions without a specification behind them yet.</summary>
    public List<string> Hotspots { get; set; } = [];

    /// <summary>
    /// Free text carried for the scaffolding layer (descriptions, provenance of an import, board
    /// comments). Never part of the descriptor roles.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>Per-type hints (description, field sketches) for scaffolding. Keyed by bare type name.</summary>
    public Dictionary<string, CuratedElement> Elements { get; set; } = [];

    public CuratedSpecifications? Specifications { get; set; }
}

public sealed class CuratedTrigger
{
    /// <summary>Http | Grpc | MessageHandler | JobScheduler | Human | External.</summary>
    public string? Kind { get; set; }

    public string? Label { get; set; }
}

public sealed class CuratedExternalSystem
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Inbound | Outbound.</summary>
    public string? Direction { get; set; }
}

public sealed class CuratedElement
{
    public string? Description { get; set; }

    /// <summary>Field name → type-or-example sketch. Hints for scaffolding, never authoritative.</summary>
    public Dictionary<string, string> Fields { get; set; } = [];
}

/// <summary>
/// The slice's scenario bodies. Only the identities (<c>{feature}/{scenario}</c>) reach the
/// descriptor, as <c>SpecificationDescriptor</c> bindings; the given/when/then bodies exist for
/// the scaffolding layer, and their shape deliberately mirrors the shipped Bobcat grammar 1:1 so
/// producing a <c>.feature</c> is a mechanical transform.
/// </summary>
public sealed class CuratedSpecifications
{
    /// <summary>
    /// The <c>{Feature}</c> half of every scenario identity here; defaults to the slice name.
    /// Must match the eventual <c>.feature</c> file's Feature name — the identity is what joins
    /// the descriptor binding, Bobcat run evidence, and a Stoat spec-identity gate.
    /// </summary>
    public string? Feature { get; set; }

    public List<CuratedScenario> Scenarios { get; set; } = [];
}

public sealed class CuratedScenario
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Prior events, oldest first. Events only — the grammar allows nothing else in a Given.</summary>
    public List<CuratedGiven> Given { get; set; } = [];

    /// <summary>The one command under test. Absent for a pure read-model assertion.</summary>
    public CuratedWhen? When { get; set; }

    /// <summary>Emitted events, one read-model assertion, or a validation failure — never mixed.</summary>
    public List<CuratedThen> Then { get; set; } = [];
}

public sealed class CuratedGiven
{
    public string Event { get; set; } = string.Empty;

    /// <inheritdoc cref="CuratedWhen.With"/>
    public Dictionary<string, string> With { get; set; } = [];

    /// <summary>
    /// A name for the stream this event belongs to, when it is not the scenario's own (issue
    /// #311). Omit for the ordinary case: every event arranged on the one stream the scenario
    /// acts against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A name, not an id.</b> Two givens carrying the same name land on the same stream, and
    /// the scaffolder mints the id from the scenario and the name together — ids stay its
    /// business, exactly as they are for the scenario's own stream.
    /// </para>
    /// <para>
    /// <b>What it is for: a fan-out read model.</b> A multi-stream projection folds many streams
    /// into one document keyed by something else — an owner, a tenant, a day — and the fold is
    /// the thing worth specifying. Without this a curated scenario is single-stream by
    /// construction, so the one behaviour that makes the projection multi-stream cannot be asked
    /// for. Pair it with <see cref="CuratedThen.Id"/>, which is the assertion half (issue #236).
    /// </para>
    /// <para>
    /// The act still runs against the scenario's own stream, and so does <c>{streamId}</c>.
    /// </para>
    /// </remarks>
    public string? Stream { get; set; }

    /// <summary>
    /// The aggregate this event belongs to, when it is not the slice's own (issue #320). Omit for
    /// the ordinary case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shipped Gherkin has always been able to say this — the aggregate is in the step text,
    /// so <c>And no events for VolunteerApplication "…"</c> re-points to another aggregate as
    /// easily as <see cref="Stream"/> re-points to another stream of the same one. The curated
    /// format could not, so a scenario the board drew was unsayable here.
    /// </para>
    /// <para>
    /// <b>Why that mattered.</b> A rule spanning two aggregates — "only an approved volunteer may
    /// accept an assignment" — needs both arranged in one scenario. Without this every arranged
    /// event landed on the acting slice's own aggregate, which for a multi-stream projection
    /// happens to WORK, because a projection routes by its identity rule and does not care what
    /// the stream is typed as. Worse than failing: the model then says something untrue and the
    /// specs pass.
    /// </para>
    /// <para>
    /// Composes with <see cref="Stream"/> — this says which type, that says which instance of it.
    /// Both together is a named second stream of another aggregate. The act still runs against the
    /// scenario's own stream, and so does <c>{streamId}</c>.
    /// </para>
    /// </remarks>
    public string? Aggregate { get; set; }
}

public sealed class CuratedWhen
{
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Field name → value, as the scaffolder writes it into the step's table. One token is
    /// understood: <c>{streamId}</c> expands to the stream this scenario runs against — the same
    /// id the scenario's <c>Given no events for …</c> step establishes (issue #235). Use it for
    /// the identity field a collapsed endpoint computes its stream from, or the act writes to a
    /// stream the <c>given:</c> events never reached.
    /// </summary>
    public Dictionary<string, string> With { get; set; } = [];
}

/// <summary>
/// Exactly one of <see cref="Event"/>, <see cref="ReadModel"/>, <see cref="ValidationFails"/> or
/// <see cref="RefusedWith"/> is set.
/// </summary>
public sealed class CuratedThen
{
    public string? Event { get; set; }

    /// <summary>
    /// The aggregate whose stream this slice STARTED, asserted with the identity it was started
    /// under (issue #360).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>event:</c> proves an event of some type was appended <b>somewhere</b>. For a minting
    /// slice that is weaker than it looks: with no arranged stream the act has no id to address, so
    /// the assertion reads whatever the store issued, and the stream the event landed on is never
    /// checked. Changing <c>Storage.StartStream&lt;T&gt;(id, e)</c> to a plain append fails nothing.
    /// </para>
    /// <para>
    /// The identity is usually the whole decision. CritterCrush's ProposeHomeCheckAppointment uses
    /// the assignment's id so a redelivered trigger collides on StartStream instead of booking a
    /// second visit — an at-least-once guarantee that lives entirely in a stream id, and that no
    /// <c>event:</c> assertion can reach.
    /// </para>
    /// </remarks>
    public string? StartsStream { get; set; }

    /// <inheritdoc cref="CuratedWhen.With"/>
    public Dictionary<string, string> With { get; set; } = [];

    public string? ReadModel { get; set; }

    /// <summary>
    /// The id of the read-model document to assert on, when it is not the scenario's own stream
    /// (issue #236) — the key a multi-stream projection routes by: an owner, a tenant, a day.
    /// Omit for a single-stream projection, whose document id IS the stream id. Understands
    /// <c>{streamId}</c> like any other scenario value.
    /// </summary>
    public string? Id { get; set; }

    /// <inheritdoc cref="CuratedWhen.With"/>
    public Dictionary<string, string> Contains { get; set; } = [];

    /// <summary>
    /// A refusal that <b>throws</b>, carrying the message the exception reads — the bus lane's
    /// form, which <c>Then validation fails with "…"</c> asserts. On an HTTP slice it still means
    /// "refused", and the scaffolder writes it as a 400: the status a <c>Validate</c> railway
    /// stub returns when nothing says otherwise.
    /// </summary>
    /// <remarks>
    /// Use <see cref="RefusedWith"/> when the status is anything but 400. The two are separate
    /// spellings because they are separate claims: this one names a message and says nothing
    /// about a transport, and a bus-dispatched refusal has no status to name.
    /// </remarks>
    public string? ValidationFails { get; set; }

    /// <summary>
    /// A refusal over HTTP that answers with a <b>stated status</b> (issue #337) — a 403, a 409,
    /// a 404 for a stream that does not exist. Only for a slice that answers over HTTP; a
    /// bus-dispatched slice refuses by throwing, which is <see cref="ValidationFails"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this the format could only say <c>validationFails:</c>, and the scaffolder turned
    /// every modelled HTTP refusal into <c>Then the response is 400</c> and a
    /// <c>ProblemDetails { Status = 400 }</c> guard TODO. A slice refusing with anything else got
    /// a confidently wrong spec and a confidently wrong stub — and a 404 for a missing stream,
    /// which every endpoint binding a non-nullable write model answers, could not be modelled at
    /// all. That left a real spec identity the model could not declare.
    /// </para>
    /// <para>
    /// <b>404 is a claim about the signature, not about a guard.</b> Declaring one is how the
    /// scaffolder learns the endpoint's write model is required: Wolverine emits the not-found
    /// guard itself for a non-nullable <c>[WriteModel]</c>, before <c>Validate</c> runs, so the
    /// scaffold binds the parameter non-nullable and writes NO guard TODO for that refusal. A
    /// hand-written null check there is unreachable code that looks load-bearing.
    /// </para>
    /// </remarks>
    public CuratedRefusal? RefusedWith { get; set; }
}

/// <summary>
/// An HTTP refusal: the status the endpoint answers with, and the human sentence saying why
/// (issue #337).
/// </summary>
/// <remarks>
/// <b>Both halves are required.</b> The status without a reason cannot scaffold a guard TODO or
/// a comment anyone can act on, and the reason without a status is exactly what
/// <see cref="CuratedThen.ValidationFails"/> already says.
/// </remarks>
public sealed class CuratedRefusal
{
    /// <summary>The HTTP status, 4xx or 5xx. There is no default: 400 is what omitting the whole
    /// node means, by writing <c>validationFails:</c> instead.</summary>
    public int Status { get; set; }

    /// <summary>
    /// Why the request was refused, in the model's own words. Reaches the scaffolded feature as a
    /// comment beside the status assertion — an HTTP refusal has nowhere to assert prose, since a
    /// ProblemDetails body is the endpoint's business — and the emitted guard TODO as its
    /// <c>Detail</c>.
    /// </summary>
    public string? Reason { get; set; }
}
