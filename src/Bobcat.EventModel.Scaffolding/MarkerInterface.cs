namespace Bobcat.EventModel.Scaffolding;

/// <summary>
/// The marker interface a multi-stream view routes by (issue #347) — one identity rule and one
/// <c>Evolve</c> instead of an <c>Identity&lt;T&gt;</c> line and an <c>Apply</c> method per event.
/// </summary>
/// <remarks>
/// <para>
/// <b>Named after the KEY, not the view.</b> Two views over the same events key differently —
/// CritterCrush's AppointmentsQueue by shelter, MyAppointments by owner — so a per-view name would
/// put two interfaces on a record that differ only in spelling, while a per-key name puts exactly
/// one marker per distinct routing question and lets both views share it.
/// </para>
/// <para>
/// <b>It requires unanimity, and says nothing when it does not have it.</b> A view whose sources
/// key by different fields cannot state one rule, so <see cref="For"/> answers null and the caller
/// keeps the per-event shape. Deriving a marker from the majority and leaving the rest as
/// <c>Apply</c> methods would be the worst of both: a reader could no longer tell, from the
/// constructor, which events the interface actually covers.
/// </para>
/// </remarks>
public sealed record MarkerInterface(string Name, string Field)
{
    /// <summary>
    /// The marker for these sources, or null when they do not agree on one identity field — or
    /// when there is only one source, where an interface is indirection buying nothing.
    /// </summary>
    public static MarkerInterface? For(IReadOnlyList<ViewSource> sources)
    {
        if (sources.Count < 2) return null;

        var fields = sources.Select(x => x.IdentityField).Distinct().ToList();
        if (fields.Count != 1) return null;

        var field = fields[0];
        if (field is not { Length: > 0 }) return null;

        return new MarkerInterface(NameFor(field), field);
    }

    /// <summary>
    /// <c>ShelterId</c> becomes <c>IShelterEvent</c>. The trailing <c>Id</c> comes off because the
    /// interface marks an EVENT about that thing, not an identifier.
    /// </summary>
    public static string NameFor(string field)
    {
        var subject = field.EndsWith("Id", StringComparison.Ordinal) && field.Length > 2
            ? field[..^2]
            : field;

        return $"I{subject}Event";
    }
}

/// <summary>
/// A <see cref="MarkerInterface"/> with the events it covers and the view slice whose file
/// declares it — one declaration per marker, wherever two views share one.
/// </summary>
public sealed record MarkerView(MarkerInterface Marker, IReadOnlyList<string> Events, string DeclaredBy);
