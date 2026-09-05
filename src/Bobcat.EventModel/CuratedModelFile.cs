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
    public List<string> ReadModels { get; set; } = [];

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
    public Dictionary<string, string> With { get; set; } = [];
}

public sealed class CuratedWhen
{
    public string Command { get; set; } = string.Empty;
    public Dictionary<string, string> With { get; set; } = [];
}

/// <summary>Exactly one of <see cref="Event"/>, <see cref="ReadModel"/>, or <see cref="ValidationFails"/> is set.</summary>
public sealed class CuratedThen
{
    public string? Event { get; set; }
    public Dictionary<string, string> With { get; set; } = [];

    public string? ReadModel { get; set; }
    public Dictionary<string, string> Contains { get; set; } = [];

    public string? ValidationFails { get; set; }
}
