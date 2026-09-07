namespace Bobcat.EventModel.Scaffolding;

/// <summary>Where an automation slice's trigger event comes from.</summary>
public enum TriggerSource
{
    /// <summary>Another slice in this model emits it, so that slice's scaffold owns the record.</summary>
    Emitted,

    /// <summary>
    /// Nothing here emits it, and an inbound <c>externalSystems:</c> edge says why: it arrives
    /// from another system, so it is an integration contract this boundary declares for itself.
    /// </summary>
    Inbound,

    /// <summary>
    /// Nothing emits it and nothing declares it external — usually a modeling gap, or a chapter
    /// extracted from a bigger board without its neighbours.
    /// </summary>
    Dangling
}

/// <summary>
/// One automation slice's trigger event and where the model says it comes from.
/// <see cref="OwnsTheContract"/> is the scaffolding consequence: nobody else declares this
/// record, so this slice's own file must.
/// </summary>
public sealed record TriggerOrigin(string Event, TriggerSource Source, string? EmittedBy, string? ExternalSystem)
{
    public bool OwnsTheContract => Source != TriggerSource.Emitted;
}

/// <summary>
/// The mirror image of <see cref="BusVisibility"/> (issue #223). Bus visibility asks where a
/// slice's published message <em>goes</em>; this asks where its trigger event <em>came from</em>,
/// and the model answers with the same vocabulary — an <c>externalSystems:</c> edge, read on the
/// inbound side this time.
/// </summary>
/// <remarks>
/// Three of eleven slices in an ordinary chapter hit this: an automation triggered by an event
/// raised in a different chapter of the board. The scaffolder faithfully emitted
/// <c>Handle(HomeCheckAssignmentAccepted trigger, …)</c> and nothing anywhere declared that
/// record, so the generated code did not compile until a human worked out that the trigger was an
/// inbound integration contract.
///
/// The record is now declared either way — a scaffold always compiles (issue #226) — but what the
/// model says about it differs. An inbound edge makes the declaration a stated boundary contract,
/// documented as one. Without an edge it is a scaffold-time warning, the same kind #218 raises for
/// a published command nothing handles: a trigger nothing produces is usually a modeling gap.
/// </remarks>
public static class TriggerOrigins
{
    /// <summary>
    /// The trigger origin for an automation slice, or null for anything else — a Command slice's
    /// trigger type is its own command, which its own scaffold already declares.
    /// </summary>
    public static TriggerOrigin? Resolve(CuratedModelFile model, CuratedSlice slice)
    {
        if (slice.Pattern != "Automation") return null;

        var trigger = SliceScaffolder.TriggerFor(slice);

        // "Emitted" means another slice's scaffold actually declares a record of that name, and
        // only `events:` does that unconditionally — a `messages:` entry is declared by its
        // publisher only when it leaves the system, and by the handling slice otherwise, which is
        // BusVisibility's question rather than this one.
        var emitter = model.Slices.FirstOrDefault(x => !ReferenceEquals(x, slice) && x.Events.Contains(trigger));
        if (emitter is not null)
        {
            return new TriggerOrigin(trigger, TriggerSource.Emitted, emitter.Name, ExternalSystem: null);
        }

        // The slice's own events, when the model gave no trigger label at all: its own file
        // declares the record, so there is nothing to own and nothing to warn about.
        if (slice.Events.Contains(trigger))
        {
            return new TriggerOrigin(trigger, TriggerSource.Emitted, slice.Name, ExternalSystem: null);
        }

        var inbound = slice.ExternalSystems.FirstOrDefault(x => x.Direction == "Inbound");
        return inbound is null
            ? new TriggerOrigin(trigger, TriggerSource.Dangling, EmittedBy: null, ExternalSystem: null)
            : new TriggerOrigin(trigger, TriggerSource.Inbound, EmittedBy: null, ExternalSystem: inbound.Name);
    }

    /// <summary>The doc comment a declared trigger contract carries, or null when nobody here owns one.</summary>
    public static string? ContractComment(TriggerOrigin origin)
        => origin.Source switch
        {
            TriggerSource.Inbound =>
                $"Inbound integration contract: {origin.Event} arrives from {origin.ExternalSystem}, and no\n"
                + "slice in this model emits it — so this is the boundary's own copy of its shape. Version it\n"
                + $"rather than edit it: a breaking change is {origin.Event}V2, never a changed field here.",
            TriggerSource.Dangling =>
                $"Trigger contract for {origin.Event}, declared here so the scaffold compiles. See the\n"
                + "warning above: the model does not say where this event comes from.",
            _ => null
        };

    /// <summary>The scaffold-time warning for a dangling trigger, or null when the model accounts for it.</summary>
    public static string? Warning(TriggerOrigin origin, CuratedSlice slice)
        => origin.Source == TriggerSource.Dangling
            ? $"slice '{slice.Name}' is triggered by '{origin.Event}', but no slice in this model emits it and no inbound `externalSystems:` edge says it arrives from another system. A trigger nothing produces is usually a modeling gap — or a sign the chapter was extracted from a bigger board without its neighbours. Add the emitting slice, or an inbound edge naming the system it comes from."
            : null;

    /// <summary>Every dangling-trigger warning in the model — the programmatic channel for a scaffolding front-end.</summary>
    public static IReadOnlyList<string> Warnings(CuratedModelFile model)
        => model.Slices
            .Select(slice => (Slice: slice, Origin: Resolve(model, slice)))
            .Where(x => x.Origin is not null)
            .Select(x => Warning(x.Origin!, x.Slice))
            .OfType<string>()
            .ToList();
}
