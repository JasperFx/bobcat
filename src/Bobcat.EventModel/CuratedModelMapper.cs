using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;

namespace Bobcat.EventModel;

/// <summary>
/// Maps a validated <see cref="CuratedModelFile"/> onto the JasperFx descriptor vocabulary.
/// Roles only — elements and edges are computed upstream on every read, and stamping a graph
/// here would be the "second opinion" the descriptor design exists to prevent.
/// </summary>
/// <remarks>
/// Declared types do not exist yet, so every type reference becomes a name-only
/// <see cref="TypeDescriptor"/> whose <c>FullName</c> is synthesized as
/// <c>{namespace}.{Name}</c> (or the bare name without a namespace) and whose assembly is empty.
/// Once code exists, the Derived rung's real types win each role wholesale; a mismatch between a
/// declared list and the derived one surfaces as a <c>SourceDisagreement</c> hotspot rather than
/// silently vanishing — which is the feature, not a bug: "the model says X, the code does Y".
/// </remarks>
public static class CuratedModelMapper
{
    public static EventModelDescriptor ToDescriptor(CuratedModelFile file)
    {
        var slices = file.Slices.Select(x => toSlice(x, file.Namespace)).ToList();
        return new EventModelDescriptor(file.Model, slices);
    }

    private static EventModelSliceDescriptor toSlice(CuratedSlice slice, string? @namespace)
    {
        TypeDescriptor type(string name) => new(name, @namespace is null ? name : $"{@namespace}.{name}", string.Empty);
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
            HandlerType: slice.Handler is null ? null : type(slice.Handler),
            EmittedEvents: types(slice.Events),
            ProjectionTypes: types(slice.Projections),
            ReadModelTypes: types(slice.ReadModels))
        {
            Pattern = parse<SlicePattern>(slice.Pattern),
            TriggerKind = parse<TriggerKind>(slice.Trigger?.Kind),
            Domain = slice.Domain,
            AggregateTypes = types(slice.Aggregates),
            PublishedMessages = types(slice.Messages),
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
