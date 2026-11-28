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
        => ToDescriptor(file, layout, CSharpModelWriter.Shapes(file, layout?.RootNamespace));

    /// <summary>
    /// Map <paramref name="file"/> with the field shapes the stubs were written with
    /// (<see cref="CSharpModelWriter.Output.Shapes"/>, bobcat#851), so the descriptor declares exactly the members
    /// the code has — strong-typed identities and nested records included.
    /// </summary>
    public static EventModelDescriptor ToDescriptor(ImportedEventModel file, ModelLayout? layout, IReadOnlyList<CSharpModelWriter.WrittenShape> shapes)
    {
        var slices = file.Slices.Select(x => toSlice(x, file.EventStreams, layout)).ToList();

        // bobcat#849: the tag catalogue is what the definition's model.Tag(…) calls say — the tags the slices use,
        // with the value type where the source gave one — which is also what the builder folds from TaggedBy/By
        var used = file.Slices.SelectMany(s => s.EventTags.Values.SelectMany(x => x).Concat(s.Reads.Select(r => r.Tag)))
            .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var tags = file.Tags.Keys.Where(used.Contains).Concat(used.Where(x => !file.Tags.ContainsKey(x)))
            .Select(name => new EventTagDescriptor(name, null)
            {
                InnerType = file.Tags.GetValueOrDefault(name) is { Length: > 0 } inner && CSharpModelWriter.Primitives.TryGetValue(inner, out var clr)
                    ? TypeDescriptor.For(clr)
                    : null,
            })
            .ToList();

        return new EventModelDescriptor(file.Model, slices)
        {
            Shapes = shapes.Where(x => x.Fields.Count > 0).Select(x => toShape(x, shapes, layout)).ToList(),
            Tags = tags,
        };
    }

    /// <summary>
    /// One written type's fields as a <see cref="TypeShapeDescriptor"/> (jasperfx#1299): a primitive is the CLR type
    /// the generated <c>OfType&lt;T&gt;()</c> names, anything else a name-only declaration where the import put it.
    /// </summary>
    private static TypeShapeDescriptor toShape(CSharpModelWriter.WrittenShape shape, IReadOnlyList<CSharpModelWriter.WrittenShape> all, ModelLayout? layout)
    {
        TypeDescriptor declared(string name)
        {
            // A nested record lives in its owner's namespace, an identity type in the root, a stub where the layout put it
            var written = all.FirstOrDefault(x => x.Type == name);
            var ns = written?.Namespace ?? (layout is not null && layout.Contains(name) ? null : layout?.RootNamespace);
            return new TypeDescriptor(name, written is not null ? $"{written.Namespace}.{name}" : layout?.FullNameOf(name) ?? (ns is null ? name : $"{ns}.{name}"), string.Empty);
        }

        TypeDescriptor fieldType(string type)
            => CSharpModelWriter.Primitives.TryGetValue(type, out var clr) ? TypeDescriptor.For(clr) : declared(type);

        return new TypeShapeDescriptor(declared(shape.Type), shape.Fields.Select(f => new FieldDescriptor(f.Name, fieldType(f.ElementType))
        {
            Cardinality = f.Many ? FieldCardinality.Many : FieldCardinality.One,
            Optional = f.Optional,
            Identity = f.Identity,
            Tag = f.Tag,
            Generated = f.Generated,
            Pii = f.Pii,
            Mapping = f.MappedFromType is { Length: > 0 } source && f.MappedFromField is { Length: > 0 } member
                ? new FieldMappingDescriptor(declared(source), member)
                : null,
            Description = f.Description,
        }).ToList());
    }

    /// <summary>
    /// The stream lane of each event the slice emits (jasperfx#1203, bobcat#845): the aggregate
    /// <see cref="ImportedEventModel.EventStreams"/> puts the event on, when that aggregate is one the
    /// slice decides against or starts — the rule <c>EventModelSliceBuilder.OnStream</c> enforces, so the
    /// descriptor written here and the one the generated definition builds agree. An event on some other
    /// stream, or on none, keeps no lane and tooling infers it as before.
    /// </summary>
    public static IEnumerable<(string Event, string Aggregate)> StreamLanes(CuratedSlice slice, IReadOnlyDictionary<string, string> eventStreams)
    {
        var aggregates = new[] { slice.StartsStream }.Concat(slice.Aggregates)
            .OfType<string>().Select(CSharpModelWriter.Identifiers.Sanitize).Where(x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var @event in slice.Events)
        {
            var stream = eventStreams.GetValueOrDefault(CSharpModelWriter.Identifiers.Sanitize(@event));
            if (stream is { Length: > 0 } && aggregates.Contains(CSharpModelWriter.Identifiers.Sanitize(stream)))
            {
                yield return (@event, stream);
            }
        }
    }

    private static EventModelSliceDescriptor toSlice(CuratedSlice slice, IReadOnlyDictionary<string, string> eventStreams, ModelLayout? layout)
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

        // bobcat#849: what the generated Decides / Reads…By / UntaggedProjection calls build
        var sanitize = CSharpModelWriter.Identifiers.Sanitize;
        var deciders = slice.Reads.Where(r => r.Kind == "Model").Select(r => sanitize(r.Type)).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var projectionReads = slice.Reads.Where(r => r.Kind != "Model").Select(r => sanitize(r.Type)).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var untagged = slice.UntaggedProjections.Select(sanitize).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var readsFrom = slice.ReadsFrom.Select(sanitize).Concat(projectionReads).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        return new EventModelSliceDescriptor(
            slice.Name,
            slice.Trigger?.Label,
            TriggerType: null,
            CommandType: slice.Command is null ? null : type(slice.Command),
            HandlerType: slice.Handler is null ? null : beside(slice.Handler, slice.Command),
            EmittedEvents: types(slice.Events),
            ProjectionTypes: slice.Projections.Select(x => beside(x, slice.ReadModels.FirstOrDefault())).Concat(untagged.Select(type)).ToList(),
            ReadModelTypes: types(slice.ReadModels))
        {
            Pattern = parse<SlicePattern>(slice.Pattern),
            TriggerKind = parse<TriggerKind>(slice.Trigger?.Kind),
            Domain = slice.Domain,
            Chapter = slice.Chapter,
            AggregateTypes = types(slice.Aggregates),
            EmittedEventStreams = StreamLanes(slice, eventStreams).Select(x => new EmittedEventStream(type(x.Event), type(x.Aggregate))).ToList(),
            StartsStream = slice.StartsStream is null ? null : type(slice.StartsStream),
            PublishedMessages = types(slice.Messages),
            ConsumedEvents = types(slice.ConsumedEvents),
            ReadsFrom = readsFrom.Select(type).ToList(),
            DeciderModel = deciders.Count > 0 ? type(deciders[0]) : null,
            AggregateDeclaration = deciders.Count > 0 ? JasperFx.Events.EventModeling.AggregateDeclaration.DeciderModel : null,
            EventTags = slice.Events.Select(sanitize)
                .Where(e => slice.EventTags.Any(x => sanitize(x.Key) == e && x.Value.Count > 0))
                .Select(e => new EmittedEventTags(type(e), slice.EventTags.First(x => sanitize(x.Key) == e).Value.Distinct(StringComparer.Ordinal).ToList()))
                .ToList(),
            Reads = slice.Reads
                .Where(r => sanitize(r.Type).Length > 0 && r.Tag.Length > 0)
                .Select(r => new BoundaryReadDescriptor(r.Kind == "Model" ? BoundaryReadKind.Model : BoundaryReadKind.Projection, type(sanitize(r.Type)), r.Tag,
                    r.Member is { Length: > 0 } ? sanitize(Emlang.EmlangImport.PascalName(r.Member)) : null))
                .Distinct()
                .ToList(),
            UntaggedProjections = untagged.Select(type).ToList(),
            RejectionReasons = slice.RejectionReasons.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToList(),
            ExternalSystems = slice.ExternalSystems
                .Select(x => new ExternalSystemDescriptor(x.Name, parse<ExternalSystemDirection>(x.Direction) ?? ExternalSystemDirection.Inbound))
                .ToList(),
            Hotspots = slice.Hotspots.Select(HotspotDescriptor.Prose).ToList(),
            Specifications = specifications,
            // bobcat#851 / jasperfx#1298: the board's prose, as the generated definition's Describe calls say it
            Description = slice.Description is { Length: > 0 } description ? description.Trim() : null,
            ElementDescriptions = slice.Elements
                .Where(x => x.Value.Description is { Length: > 0 })
                .Select(x => (Name: CSharpModelWriter.Identifiers.Sanitize(Emlang.EmlangImport.PascalName(x.Key)), Text: x.Value.Description!.Trim()))
                .Where(x => x.Name.Length > 0)
                .Select(x => new ElementDescription(type(x.Name), x.Text))
                .ToList(),
        };
    }

    private static TEnum? parse<TEnum>(string? value) where TEnum : struct, Enum
        => value is not null && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : null;
}
