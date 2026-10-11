using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;

namespace Bobcat.EventModel;

/// <summary>
/// Maps a validated <see cref="ImportedEventModel"/> onto the JasperFx descriptor vocabulary.
/// Roles only — elements and edges are computed upstream on every read, and stamping a graph
/// here would be the "second opinion" the descriptor design exists to prevent.
/// </summary>
/// <remarks>
/// <para>
/// Declared types do not exist yet, so every type reference becomes a name-only
/// <see cref="TypeDescriptor"/> whose assembly is empty. Its <c>FullName</c> is where the type
/// <em>will</em> be: the namespace <see cref="ModelLayout"/> gives it, which is the namespace
/// <see cref="CSharpModelWriter"/> writes its stub into (bobcat#383). Before that the mapper
/// synthesized <c>{namespace}.{Name}</c> on its own, so a type that really lived in a chapter's
/// namespace was declared somewhere else, and Stoat's canvas drew one aggregate as two swimlanes
/// keyed on the two full names. The rule is now asked, not re-derived.
/// </para>
/// <para>
/// The two roles <see cref="ModelLayout"/> never places, because neither is stubbed, follow
/// <c>wolverine scaffold</c>: a handler is appended to its command's file, so it shares the
/// command's namespace, and a projection lives beside the slice's read model. Without a namespace
/// at all, the full name is the bare name.
/// </para>
/// <para>
/// Once code exists, the Derived rung's real types win each role wholesale; a mismatch between a
/// declared list and the derived one surfaces as a <c>SourceDisagreement</c> hotspot rather than
/// silently vanishing, which is the feature, not a bug: "the model says X, the code does Y".
/// </para>
/// </remarks>
public static class ImportedModelMapper
{
    /// <summary>Map <paramref name="file"/>, laying its types out under its own namespace.</summary>
    public static EventModelDescriptor ToDescriptor(ImportedEventModel file)
        => ToDescriptor(file, file.Namespace is { Length: > 0 } ns ? ModelLayout.For(file, ns) : null);

    /// <summary>
    /// Map <paramref name="file"/> with the full names <paramref name="layout"/> gives its types. Pass
    /// the layout the stubs were written with (<see cref="CSharpModelWriter.Output.Layout"/>), so the
    /// declared full names are the ones the code will have; null keeps every name bare.
    /// </summary>
    public static EventModelDescriptor ToDescriptor(ImportedEventModel file, ModelLayout? layout)
    {
        var slices = file.Slices.Select(x => toSlice(x, layout)).ToList();
        return new EventModelDescriptor(file.Model, slices);
    }

    private static EventModelSliceDescriptor toSlice(CuratedSlice slice, ModelLayout? layout)
    {
        TypeDescriptor type(string name) => new(name, layout?.FullNameOf(name) ?? name, string.Empty);
        TypeDescriptor beside(string name, string? neighbour)
            => new(name, layout?.FullNameOf(name, besideType: neighbour, chapter: slice.Chapter) ?? name, string.Empty);
        IReadOnlyList<TypeDescriptor> types(List<string> names) => names.Select(type).ToList();

        var feature = slice.Specifications?.Feature ?? slice.Name;
        var specifications = slice.Specifications?.Scenarios
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new SpecificationDescriptor($"{feature}/{x.Name}"))
            .ToList() ?? [];

        return new EventModelSliceDescriptor(
            slice.Name,
            slice.Trigger?.Label,
            TriggerType: null,
            CommandType: slice.Command is null ? null : type(slice.Command),
            HandlerType: slice.Handler is null ? null : beside(slice.Handler, slice.Command),
            EmittedEvents: types(slice.Events),
            ProjectionTypes: slice.Projections.Select(x => beside(x, slice.ReadModels.FirstOrDefault())).ToList(),
            ReadModelTypes: types(slice.ReadModels))
        {
            Pattern = parse<SlicePattern>(slice.Pattern),
            TriggerKind = parse<TriggerKind>(slice.Trigger?.Kind),
            Domain = slice.Domain,
            Chapter = slice.Chapter,
            AggregateTypes = types(slice.Aggregates),
            StartsStream = slice.StartsStream is null ? null : type(slice.StartsStream),
            PublishedMessages = types(slice.Messages),
            ConsumedEvents = types(slice.ConsumedEvents),
            ReadsFrom = types(slice.ReadsFrom),
            ExternalSystems = slice.ExternalSystems
                .Select(x => new ExternalSystemDescriptor(x.Name, parse<ExternalSystemDirection>(x.Direction) ?? ExternalSystemDirection.Inbound))
                .ToList(),
            Hotspots = slice.Hotspots.Select(HotspotDescriptor.Prose).ToList(),
            Specifications = specifications,
        };
    }

    private static TEnum? parse<TEnum>(string? value) where TEnum : struct, Enum
        => value is not null && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : null;
}
