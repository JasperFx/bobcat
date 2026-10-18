using System.Text;
using JasperFx.CodeGeneration;

namespace Bobcat.EventModel;

/// <summary>
/// Writes an imported event model out as <b>C#</b> (issue #405): one field-less stub record per
/// command, event, aggregate and view on the board, plus one <c>EventModelDefinition</c> that
/// declares the slices through the JasperFx.Events fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why C# and not YAML.</b> jasperfx#955 settled that YAML is not an authoring syntax: the only
/// YAML Bobcat reads is the Event Modeling platform's own, on import. So an import no longer
/// produces a file to hand-edit — it produces the code the design starts from, which specs can be
/// written against immediately and which a Wolverine <c>scaffold</c> command can later fill in.
/// The imported model is red until the behaviour exists, and that is the point.
/// </para>
/// <para>
/// <b>A stub carries only the fields the board names, plus an <c>Id</c> where it names no identity.</b>
/// When a step declares props (<c>email: string</c>) or a test gives an element example values, the
/// stub is a positional record of exactly those fields, typed from the declaration or inferred from
/// the sample, and <c>string</c> when neither says more (issue #422). A command, aggregate or read
/// model whose model marks no identity also gets <c>Guid Id</c> (bobcat#438): that is Wolverine's own
/// naming convention, so a generated specification can address the stream and a handler's
/// <c>[WriteModel]</c> resolves it with nothing declared. Events get no invented fields.
/// </para>
/// <para>
/// <b>One file per slice, in a folder and namespace per chapter</b> (bobcat#441, see
/// <see cref="ModelLayout"/>). The definition stays one file in the root namespace.
/// </para>
/// <para>
/// <b>Everything goes through <see cref="ISourceWriter"/></b>, the same rule Wolverine's
/// <c>scaffold</c> command follows. That is what jasperfx#956's whitespace fixes are for: no blank
/// line before a closing brace, <c>END</c> only as a whole directive, and backticks for double
/// quotes. Hand-rolled string building is how generated code acquires the stray blank lines nobody
/// can explain.
/// </para>
/// <para>
/// <b>Declared by name, not by type, wherever the board only has a name.</b> Every fluent role
/// method has a string overload, and a string becomes a <c>TypeDescriptor(name, name, "")</c> whose
/// empty assembly makes the merge compare by <c>Name</c> — which is exactly how a declared slice
/// folds into the derived one once the real type exists. The generated definition therefore uses
/// <c>typeof</c>-free string overloads for the roles and <c>&lt;T&gt;</c> only where this writer
/// emitted the stub itself and so knows the type will compile.
/// </para>
/// </remarks>
public static class CSharpModelWriter
{
    /// <summary>What <see cref="Write"/> produced: the stub files and the definition file.</summary>
    /// <param name="StubFiles">
    /// The stubs, one file per slice in a folder per chapter (bobcat#441), each path relative to the
    /// output directory: <c>Features/{Chapter}/{Slice}.cs</c> holds the slice's command (or read
    /// model) and the events it emits, and a type no slice produces gets a file of its own. This is
    /// the file <c>wolverine scaffold</c> adds the handler to, so a command and its handler end up
    /// side by side rather than in one list nobody wants to work in.
    /// </param>
    /// <param name="Definition">The <c>EventModelDefinition</c> subclass, one file in the root namespace.</param>
    /// <param name="StubCount">How many stub types were written.</param>
    /// <param name="Layout">Where each type went, for anything that has to <c>using</c> it.</param>
    public sealed record Output(IReadOnlyList<GeneratedFile> StubFiles, string Definition, int StubCount, ModelLayout Layout)
    {
        /// <summary>
        /// bobcat#448: with <c>perChapter</c>, one <c>EventModelDefinition</c> per chapter, each beside its
        /// chapter's stubs (<c>Features/{Chapter}/{Chapter}Model.cs</c>) and a root one for slices with no
        /// chapter; <see cref="Definition"/> is then empty. Empty for a single definition.
        /// </summary>
        public IReadOnlyList<GeneratedFile> DefinitionFiles { get; init; } = [];

        /// <summary>
        /// The strong-typed identifiers written for this model (bobcat#512), and what each could not
        /// type. Empty without <c>--strong-typed-ids</c>. The structs themselves are an ordinary entry
        /// in <see cref="StubFiles"/> (<c>Identities.cs</c>), so the additive re-import adds a new one
        /// to an existing file and never touches one somebody edited.
        /// </summary>
        public StrongTypedIds Identities { get; init; } = StrongTypedIds.None;
    }

    public static Output Write(ImportedEventModel model, string? namespaceName = null)
        => Write(model, namespaceName, null);

    /// <summary>
    /// What specifications generated from the model (bobcat#423) need of the stubs beyond the
    /// model's own: the streams they arrange events on, and the documents they load by id.
    /// </summary>
    /// <param name="Streams">
    /// Each becomes a stream type, <c>public class Order { public Guid Id { get; set; } }</c>, unless
    /// the model already stubs it.
    /// </param>
    /// <param name="Documents">Each read model loaded by id, which gains a <c>Guid Id</c> when it names none.</param>
    /// <param name="Elements">
    /// Each command, event or view an example names that no slice does, such as an event a test
    /// arranges from another part of the model; stubbed like any other, with the fields it is given.
    /// </param>
    /// <param name="Fields">Fields an example names that the model's hints never reached, by stub.</param>
    public sealed record StubAdditions(IReadOnlyList<string> Streams, IReadOnlyList<string> Documents,
        IReadOnlyList<string>? Elements = null, IReadOnlyDictionary<string, IReadOnlyList<StubField>>? Fields = null);

    /// <inheritdoc cref="Write(ImportedEventModel, string?)"/>
    /// <param name="additions">What generated specifications need of the stubs; nothing when null.</param>
    public static Output Write(ImportedEventModel model, string? namespaceName, StubAdditions? additions)
        => Write(model, namespaceName, additions, perChapter: false);

    /// <inheritdoc cref="Write(ImportedEventModel, string?, StubAdditions?)"/>
    /// <param name="perChapter">
    /// One definition per chapter (bobcat#448), with the chapter and the chapter's dominant aggregate
    /// said once at the top (<c>model.InChapter</c>, <c>model.ForAggregate&lt;T&gt;()</c>) and no <c>Name</c>,
    /// so every chapter joins the application's model (jasperfx#992).
    /// </param>
    public static Output Write(ImportedEventModel model, string? namespaceName, StubAdditions? additions, bool perChapter)
        => Write(model, namespaceName, additions, perChapter, strongTypedIds: false);

    /// <inheritdoc cref="Write(ImportedEventModel, string?, StubAdditions?, bool)"/>
    /// <param name="strongTypedIds">
    /// Give each aggregate a strong-typed identifier (bobcat#512): <c>readonly record struct OrderId(Guid
    /// Value)</c> in <c>Identities.cs</c>, every <c>Id</c> and <c>{Aggregate}Id</c> member that addresses
    /// that stream typed with it, and <c>.IdentifiedBy&lt;OrderId&gt;()</c> on the aggregate's
    /// declaration. Off by default: an undeclared identity is a <see cref="Guid"/>, and typing one is a
    /// modelling choice no board expresses.
    /// </param>
    public static Output Write(ImportedEventModel model, string? namespaceName, StubAdditions? additions, bool perChapter,
        bool strongTypedIds)
        => Write(model, namespaceName, additions, perChapter,
            strongTypedIds ? StrongTypedIds.For(model, StubNames(model)) : StrongTypedIds.None);

    /// <inheritdoc cref="Write(ImportedEventModel, string?, StubAdditions?, bool, bool)"/>
    /// <param name="identities">
    /// The strong-typed identifiers to write, already decided — what a caller that has also handed them
    /// to the spec writer (in the Wolverine repo since bobcat#744) passes, so the stubs and the specifications cannot be written against
    /// two different answers.
    /// </param>
    public static Output Write(ImportedEventModel model, string? namespaceName, StubAdditions? additions, bool perChapter,
        StrongTypedIds identities)
    {
        var ns = namespaceName
                 ?? (string.IsNullOrWhiteSpace(model.Namespace) ? null : model.Namespace)
                 ?? Identifiers.Sanitize(model.Model);

        var stubs = StubNames(model)
            .Concat(additions?.Elements ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var streams = (additions?.Streams ?? [])
            .Where(x => !stubs.Contains(x, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var documents = (additions?.Documents ?? []).ToHashSet(StringComparer.Ordinal);

        var extra = additions?.Fields ?? new Dictionary<string, IReadOnlyList<StubField>>();
        var layout = Layout(model, ns, additions);

        var ids = identities;

        // An identity type IS a type of that name, so a stub of the same name would be a second
        // declaration of it. StrongTypedIds skips an aggregate whose id name the MODEL takes; this
        // catches the same collision with a type only an example names, where the identity wins
        // because the stubs and the definition are already written against it.
        if (ids.Any)
        {
            var minted = ids.Declarations.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            stubs = stubs.Where(x => !minted.Contains(x)).ToList();
            streams = streams.Where(x => !minted.Contains(x)).ToList();
        }

        var stubFiles = writeStubs(layout, model, stubs, streams, documents, extra, ids).ToList();
        if (ids.Any) stubFiles.Insert(0, writeIdentities(ns, ids));

        if (!perChapter)
        {
            return new Output(stubFiles, writeDefinition(ns, model, stubs, layout, ids), stubs.Count + streams.Count, layout)
            {
                Identities = ids
            };
        }

        return new Output(stubFiles, "", stubs.Count + streams.Count, layout)
        {
            DefinitionFiles = writeChapterDefinitions(ns, model, stubs, layout, ids),
            Identities = ids
        };
    }

    /// <summary>
    /// Where <see cref="Write(ImportedEventModel, string?, StubAdditions?)"/> puts every type, so a
    /// specification writer can <c>using</c> the same namespaces before the stubs are written.
    /// </summary>
    public static ModelLayout Layout(ImportedEventModel model, string ns, StubAdditions? additions = null)
        => ModelLayout.For(model, ns, (additions?.Streams ?? []).Concat(additions?.Elements ?? []));

    /// <summary>
    /// Every type name the board named, in the order a reader would expect: aggregates, then
    /// commands, then events, then views.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Four kinds, because those are the four the issue names</b> — command, event, aggregate,
    /// view. A handler is deliberately NOT stubbed: a handler is behaviour, and an empty one is the
    /// thing the <c>scaffold</c> command exists to write properly. Nor are external systems, which
    /// are not types at all.
    /// </para>
    /// <para>
    /// <b>Consumed events are included, and that matters.</b> A View slice's <c>consumedEvents</c>
    /// routinely names events emitted by a slice in another chapter, so leaving them out produced a
    /// definition referring to names with no stub — which compiles, because the roles are declared
    /// by string, and then quietly loses the type when someone switches to the generic overload.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> StubNames(ImportedEventModel model)
    {
        var names = new List<string>();

        void take(IEnumerable<string?> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                var name = Identifiers.Sanitize(candidate!);
                if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            }
        }

        take(model.Slices.SelectMany(AggregatesOf));
        take(model.Slices.Select(s => s.Command));
        take(model.Slices.SelectMany(s => s.Events));
        take(model.Slices.SelectMany(s => s.ConsumedEvents));
        take(model.Slices.SelectMany(s => s.Messages));
        take(model.Slices.SelectMany(s => s.ReadModels));
        take(model.Slices.SelectMany(s => s.ReadsFrom));

        return names;
    }

    private static IReadOnlyList<GeneratedFile> writeStubs(ModelLayout layout, ImportedEventModel model, IReadOnlyList<string> stubs,
        IReadOnlyList<string> streams, HashSet<string> documents, IReadOnlyDictionary<string, IReadOnlyList<StubField>> extra,
        StrongTypedIds ids)
    {
        var aggregates = model.Slices.SelectMany(AggregatesOf)
            .Select(x => Identifiers.Sanitize(x ?? ""))
            .Concat(streams)
            .ToHashSet(StringComparer.Ordinal);

        var fields = stubs.Concat(streams).Distinct(StringComparer.Ordinal).ToDictionary(x => x, x =>
        {
            var named = FieldsOf(model, x, ids)
                .Concat(Retyped(x, extra.GetValueOrDefault(x) ?? [], ids))
                .ToList();

            // bobcat#444: a command deciding against several streams names each one's identity,
            // {Aggregate}Id, which is how each [WriteModel] IEventStream<T> finds its stream
            foreach (var streamId in StreamIdFieldsOf(model, x).Where(id => named.All(f => f.Name != id)))
            {
                named.Add(new StubField(streamId, ids.IdOfMember(streamId) ?? "Guid"));
            }

            // bobcat#438: a command, aggregate or read model the model gives no identity is
            // identified by Id, Wolverine's own convention, so a specification can address it and
            // [WriteModel] resolves it with nothing declared. A document the specs load by id
            // needs one whatever its kind.
            var wantsId = documents.Contains(x) || aggregates.Contains(x) || GetsDefaultId(model, x, named);
            if (wantsId && named.All(f => f.Name != "Id")) return [new StubField("Id", DefaultIdType(model, x, ids)), .. named];

            // An Id a specification added comes first too, where a reader looks for the identity
            return named.FirstOrDefault(f => f.Name == "Id") is { } id ? [id, .. named.Where(f => f != id)] : named;
        }, StringComparer.Ordinal);

        var files = new List<GeneratedFile>();
        foreach (var group in stubs.Concat(streams).Distinct(StringComparer.Ordinal).GroupBy(layout.FileOf))
        {
            var types = group.ToList();
            using var writer = new SourceWriter();

            writer.WriteLine("// Imported from an event model by `bobcat import-event-model`.");
            writer.WriteLine("//");
            writer.WriteLine("// A stub has only the fields the model names, typed from what it declares or from its");
            writer.WriteLine("// example values, plus an Id where the model names no identity. Add the rest as the");
            writer.WriteLine("// behaviour takes shape. Nothing regenerates this file, so your edits are safe.");
            writer.BlankLine();

            var typeFields = types.SelectMany(x => fields[x]).ToList();
            if (typeFields.Count > 0)
            {
                writer.WriteLine("using System;");
                if (typeFields.Any(f => f.Type.StartsWith("List<", StringComparison.Ordinal)))
                {
                    writer.WriteLine("using System.Collections.Generic;");
                }
                writer.BlankLine();
            }

            writer.WriteLine($"namespace {layout.NamespaceOf(types[0])};");
            writer.BlankLine();

            foreach (var name in types)
            {
                foreach (var line in describe(model, name)) writer.WriteLine($"/// {line}");
                if (aggregates.Contains(name))
                {
                    if (!describe(model, name).Any()) writer.WriteLine("/// <summary>A stream the generated specifications arrange events on.</summary>");
                    writeAggregate(writer, name, fields[name], ids);
                }
                else
                {
                    writer.WriteLine(fields[name].Count == 0
                        ? $"public record {name};"
                        : $"public record {name}({string.Join(", ", fields[name].Select(x => $"{x.Type} {x.Name}"))});");
                }

                writer.BlankLine();
            }

            files.Add(new GeneratedFile(group.Key, writer.Code()));
        }

        return files;
    }

    /// <summary>
    /// An aggregate is a class with settable properties rather than a positional record: it is the
    /// write model the event store folds, and every Critter Stack store (Marten, Polecat, Fisher)
    /// builds one from an <c>Id</c> and its events the same way.
    /// </summary>
    private static void writeAggregate(ISourceWriter writer, string name, IReadOnlyList<StubField> fields, StrongTypedIds identities)
    {
        if (fields.Count == 1 && fields[0] is { Name: "Id" } id && (id.Type == "Guid" || identities.Contains(id.Type)))
        {
            writer.WriteLine($"public class {name} {{ public {id.Type} Id {{ get; set; }} }}");
            return;
        }

        writer.Write($"BLOCK:public class {name}");
        foreach (var field in fields)
        {
            writer.WriteLine($"public {field.Type} {field.Name} {{ get; set; }}{(field.Type.StartsWith("List<", StringComparison.Ordinal) ? " = [];" : field.Type == "string" ? " = string.Empty;" : "")}");
        }
        writer.FinishBlock();
    }

    /// <summary>
    /// Whether <paramref name="stub"/> gets the conventional <c>Id</c> (bobcat#438): a command,
    /// aggregate or read model the model names no identity for. An identity is a field the model
    /// marks (<c>idAttribute</c>), one already called <c>Id</c> or <c>AggregateId</c>, or the
    /// <c>{Aggregate}Id</c> of the aggregate its slice declares. Events never get one: an event's
    /// stream is where it is stored, not a field it carries.
    /// </summary>
    /// <remarks>
    /// <b>A command that only starts a stream gets none (bobcat#453).</b> Its handler mints the new
    /// stream's id, a sequential Guid, so an <c>Id</c> on the command is a member nothing reads. A
    /// specification that needs one names it in an example, which puts it in the stub's fields. The
    /// exception is a stream keyed by a <b>string</b>: a natural key cannot be minted, so the command
    /// carries it (<see cref="DefaultIdType"/> types it) and the new stream takes it.
    /// </remarks>
    public static bool GetsDefaultId(ImportedEventModel model, string stub, IReadOnlyList<StubField>? fields = null)
    {
        var commandSlices = model.Slices.Where(s => Identifiers.Sanitize(s.Command ?? "") == stub).ToList();
        var isCommand = commandSlices.Count > 0;
        var isReadModel = model.Slices.Any(s => s.ReadModels.Concat(s.ReadsFrom).Any(x => Identifiers.Sanitize(x ?? "") == stub));
        var isAggregate = model.Slices.Any(s => AggregatesOf(s).Any(x => Identifiers.Sanitize(x ?? "") == stub));
        if (!isCommand && !isReadModel && !isAggregate) return false;

        if (isCommand && !isReadModel && !isAggregate && commandSlices.All(OnlyStartsStream)
            && !commandSlices.Any(s => StreamIdentityType(model, s.StartsStream!) == "string"))
        {
            return false;
        }

        // Several streams: each is addressed by its own {Aggregate}Id, so a lone Id would say nothing
        if (StreamIdFieldsOf(model, stub).Count > 0) return false;

        fields ??= FieldsOf(model, stub);
        if (fields.Any(f => f.Name is "Id" or "AggregateId")) return false;

        var marked = model.Slices
            .SelectMany(s => s.Elements)
            .Any(x => Identifiers.Sanitize(x.Key) == stub && x.Value.Identities.Count > 0);
        if (marked) return false;

        var streamIds = commandSlices.SelectMany(s => s.Aggregates).Select(a => Identifiers.Sanitize(a ?? "") + "Id");
        return !fields.Any(f => streamIds.Contains(f.Name));
    }

    /// <summary>
    /// Whether the slice starts a stream and decides against no other (bobcat#453): its command
    /// addresses nothing that exists yet, so it needs no identity of its own.
    /// </summary>
    public static bool OnlyStartsStream(CuratedSlice slice)
        => slice.StartsStream is { Length: > 0 } && !slice.Aggregates.Any(x => x is { Length: > 0 } && x != slice.StartsStream);

    /// <summary>
    /// The type of a stream's identity: <c>string</c> when an element stored on it, or the aggregate
    /// itself, declares a string identity (a natural key such as an email), else <c>Guid</c>, which is
    /// what every Critter Stack store defaults a stream id to. An identity is a field the model marks
    /// (<c>idAttribute</c>), or else the one named <c>{Aggregate}Id</c> or <c>AggregateId</c>.
    /// </summary>
    public static string StreamIdentityType(ImportedEventModel model, string stream)
    {
        var aggregate = Identifiers.Sanitize(stream);
        var stored = model.EventStreams.Where(x => Identifiers.Sanitize(x.Value) == aggregate).Select(x => Identifiers.Sanitize(x.Key));

        foreach (var element in stored.Append(aggregate).Distinct(StringComparer.Ordinal))
        {
            var marked = model.Slices.SelectMany(s => s.Elements)
                .Where(x => Identifiers.Sanitize(x.Key) == element)
                .SelectMany(x => x.Value.Identities)
                .Select(x => Identifiers.Sanitize(Emlang.EmlangImport.PascalName(x)))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var field in FieldsOf(model, element))
            {
                var identity = marked.Count > 0
                    ? marked.Contains(field.Name)
                    : field.Name == aggregate + "Id" || field.Name == "AggregateId" || (element == aggregate && field.Name == "Id");
                if (identity && field.Type == "string") return "string";
            }
        }

        return "Guid";
    }

    /// <summary>
    /// The type of the conventional <c>Id</c> a stub is given: <c>string</c> for a command that starts
    /// a string-keyed stream and for that stream's aggregate (bobcat#453), else <c>Guid</c>.
    /// </summary>
    public static string DefaultIdType(ImportedEventModel model, string stub) => DefaultIdType(model, stub, StrongTypedIds.None);

    /// <inheritdoc cref="DefaultIdType(ImportedEventModel, string)"/>
    /// <param name="ids">
    /// The strong-typed identifiers in force (bobcat#512). When the stream this stub addresses has one,
    /// that type IS the stub's <c>Id</c> — an aggregate's own, the <c>Id</c> of a command that decides
    /// against it, and the <c>Id</c> of a read model projected from it are one identity, and Marten
    /// requires a single-stream projection's document id to be the stream's id type.
    /// </param>
    public static string DefaultIdType(ImportedEventModel model, string stub, StrongTypedIds ids)
    {
        if (ids.Any && IdentityOwnerOf(model, stub) is { } owner && ids.IdOf(owner) is { } typed) return typed;

        var started = model.Slices
            .Where(s => Identifiers.Sanitize(s.Command ?? "") == stub && OnlyStartsStream(s))
            .Select(s => s.StartsStream!)
            .Concat(model.Slices.SelectMany(AggregatesOf).Where(x => Identifiers.Sanitize(x) == stub));

        return started.Any(x => StreamIdentityType(model, x) == "string") ? "string" : "Guid";
    }

    /// <summary>
    /// The aggregate whose identity a stub's identity members ARE, or null when no single stream owns
    /// it (bobcat#512): the aggregate itself; the stream an event is stored on; the one stream a command
    /// starts or decides against; the one aggregate behind every slice that produces a read model, or —
    /// for a view slice, which names no aggregate — the one stream every event it consumes is stored on.
    /// Null where the model names several, or none: a stub whose identity belongs to nothing keeps the
    /// primitive.
    /// </summary>
    /// <remarks>
    /// The read-model arm is a correctness claim, not a convenience: a single-stream projection's
    /// document id must BE the stream's identity type, so a read model projected from <c>Order</c> and
    /// keyed by a bare <c>Guid</c> is a projection that will not register.
    /// </remarks>
    public static string? IdentityOwnerOf(ImportedEventModel model, string stub)
    {
        var name = Identifiers.Sanitize(stub ?? "");
        if (name.Length == 0) return null;

        if (model.Slices.SelectMany(AggregatesOf).Any(x => Identifiers.Sanitize(x) == name)) return name;

        if (Identifiers.Sanitize(model.EventStreams.GetValueOrDefault(name) ?? "") is { Length: > 0 } stream) return stream;

        if (single(model.Slices.Where(s => Identifiers.Sanitize(s.Command ?? "") == name)) is { } byCommand)
        {
            return byCommand;
        }

        var producing = model.Slices
            .Where(s => s.ReadModels.Concat(s.ReadsFrom).Any(x => Identifiers.Sanitize(x ?? "") == name))
            .ToList();

        return single(producing) ?? onlyStreamConsumed(model, producing);

        static string? single(IEnumerable<CuratedSlice> slices)
        {
            var aggregates = slices.SelectMany(AggregatesOf).Select(Identifiers.Sanitize)
                .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();

            return aggregates.Count == 1 ? aggregates[0] : null;
        }
    }

    /// <summary>
    /// The one stream every event these slices consume is stored on, or null when they span several or
    /// the model places none of them.
    /// </summary>
    private static string? onlyStreamConsumed(ImportedEventModel model, IReadOnlyList<CuratedSlice> slices)
    {
        var streams = slices
            .SelectMany(s => s.ConsumedEvents.Concat(s.Events))
            .Select(x => Identifiers.Sanitize(x ?? ""))
            .Where(x => x.Length > 0)
            .Select(x => model.EventStreams.GetValueOrDefault(x))
            .Select(x => Identifiers.Sanitize(x ?? ""))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return streams is [{ Length: > 0 } only] ? only : null;
    }

    /// <summary>
    /// Every aggregate a slice names: the one whose stream it starts, then the ones it decides
    /// against (bobcat#444).
    /// </summary>
    public static IEnumerable<string> AggregatesOf(CuratedSlice slice)
        => new[] { slice.StartsStream }.Concat(slice.Aggregates).OfType<string>().Where(x => x.Length > 0).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The <c>{Aggregate}Id</c> members a command needs (bobcat#444): one per stream it decides
    /// against, when it draws on more than one. A command against a single stream uses <c>Id</c>, and
    /// a stream the slice starts needs none, because the handler mints it.
    /// </summary>
    public static IReadOnlyList<string> StreamIdFieldsOf(ImportedEventModel model, string stub)
    {
        var slice = model.Slices.FirstOrDefault(s => Identifiers.Sanitize(s.Command ?? "") == stub);
        if (slice is null || AggregatesOf(slice).Count() < 2) return [];

        return slice.Aggregates.Select(a => Identifiers.Sanitize(a) + "Id").Where(x => x.Length > 2).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>One field of a stub record, as the writer emits it.</summary>
    public sealed record StubField(string Name, string Type);

    /// <summary>
    /// The fields the model names for one stub, gathered from every slice's element hints in model
    /// order: the first sketch of a field wins, and a sketch the type table does not know (a
    /// sample such as <c>order-123</c>) is a <c>string</c>.
    /// </summary>
    public static IReadOnlyList<StubField> FieldsOf(ImportedEventModel model, string stub) => fieldsOf(model, stub);

    /// <inheritdoc cref="FieldsOf(ImportedEventModel, string)"/>
    /// <param name="ids">
    /// The strong-typed identifiers in force (bobcat#512): a member called <c>{Aggregate}Id</c> takes the
    /// aggregate's identity type, which is the whole of "every OrderId field on its events and commands
    /// uses that type".
    /// </param>
    public static IReadOnlyList<StubField> FieldsOf(ImportedEventModel model, string stub, StrongTypedIds ids)
        => Retyped(stub, fieldsOf(model, stub), ids);

    /// <summary>
    /// Each of <paramref name="stub"/>'s fields with its identity type, where one applies (bobcat#512) —
    /// a member named <c>{Aggregate}Id</c>, or one the model marks as this element's identity.
    /// </summary>
    /// <remarks>
    /// Only where the field already holds what the identity type wraps, so a string the model declared
    /// is never quietly re-read as a Guid.
    /// </remarks>
    public static IReadOnlyList<StubField> Retyped(string stub, IReadOnlyList<StubField> fields, StrongTypedIds ids)
        => !ids.Any
            ? fields
            : fields.Select(x => ids.IdOfMember(stub, x.Name) is { } typed && ids.Inner(typed) == x.Type
                ? x with { Type = typed }
                : x).ToList();

    private static IReadOnlyList<StubField> fieldsOf(ImportedEventModel model, string stub)
    {
        var fields = new List<StubField>();

        foreach (var slice in model.Slices)
        {
            foreach (var (typeName, element) in slice.Elements)
            {
                if (Identifiers.Sanitize(typeName) != stub) continue;

                foreach (var (fieldName, sketch) in element.Fields)
                {
                    var name = Identifiers.Sanitize(Emlang.EmlangImport.PascalName(fieldName));
                    if (name.Length == 0) continue;

                    // A positional member may not share its record's name
                    if (name == stub) name += "Value";
                    if (fields.Any(x => x.Name == name)) continue;

                    fields.Add(new StubField(name, typeOf(name, sketch)));
                }
            }
        }

        return fields;
    }

    /// <summary>
    /// A declared type wins. Otherwise an identity (a name ending in <c>Id</c>) is a <see cref="Guid"/>,
    /// whatever its example looks like, because a symbolic sample such as <c>order-123</c> stands
    /// for an id and Marten streams default to Guid ids (bobcat#423). Anything else is inferred from
    /// its sample, and is a string when the sample says nothing more.
    /// </summary>
    private static string typeOf(string field, string sketch)
    {
        if (CuratedFieldTypes.IsDeclaration(sketch) && CuratedFieldTypes.TryInfer(sketch, out var declared)) return declared;
        if (field.EndsWith("Id", StringComparison.Ordinal)) return "Guid";
        return CuratedFieldTypes.TryInfer(sketch, out var inferred) ? inferred : "string";
    }

    /// <summary>
    /// A stub's doc comment: what the board said this type is, which is the one thing the name
    /// alone does not carry.
    /// </summary>
    private static IEnumerable<string> describe(ImportedEventModel model, string name)
    {
        var roles = new List<string>();

        void note(string role, Func<CuratedSlice, IEnumerable<string>> pick)
        {
            var slices = model.Slices
                .Where(s => pick(s).Any(x => Identifiers.Sanitize(x ?? "") == name))
                .Select(s => s.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (slices.Count > 0) roles.Add($"{role} {string.Join(", ", slices)}");
        }

        note("the stream started by", s => s.StartsStream is null ? [] : [s.StartsStream]);
        note("the aggregate behind", s => s.Aggregates);
        note("the command of", s => s.Command is null ? [] : [s.Command]);
        note("emitted by", s => s.Events);
        note("consumed by", s => s.ConsumedEvents);
        note("published by", s => s.Messages);
        note("the read model of", s => s.ReadModels);
        note("read by", s => s.ReadsFrom);

        if (roles.Count == 0) yield break;

        yield return "<summary>";
        foreach (var role in roles) yield return role + ".";
        yield return "</summary>";
    }

    /// <summary>
    /// The strong-typed identifiers, one file in the root namespace (bobcat#512).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One file, in the root namespace</b>, because an identity is shared: a chapter's stubs and
    /// another chapter's both name <c>OrderId</c>, and the root namespace encloses every chapter's, so
    /// nothing needs a <c>using</c> for it.
    /// </para>
    /// <para>
    /// <b>The shape is Wolverine's generated one</b>: a positional <c>readonly record struct</c> over
    /// <c>Value</c> (which is what Marten, Polecat and Fisher recognise — a constructor for the inner
    /// value and a public <c>Value</c> getter), a <c>ToString</c> that is the value rather than the
    /// record's generated <c>OrderId { Value = ... }</c>, <see cref="IParsable{T}"/> so a route
    /// parameter binds, and a nested JSON converter so the wire carries the value rather than an object
    /// wrapping it. All four are needed by something an imported model routinely does — an HTTP slice
    /// posts the command and reads the id back — so a bare struct would be a type the first endpoint
    /// breaks on.
    /// </para>
    /// </remarks>
    private static GeneratedFile writeIdentities(string ns, StrongTypedIds ids)
    {
        using var writer = new SourceWriter();

        writer.WriteLine("// Imported from an event model by `bobcat import-event-model --strong-typed-ids`.");
        writer.WriteLine("//");
        writer.WriteLine("// One identity type per aggregate, wrapping the identity its stream would otherwise have.");
        writer.WriteLine("// Nothing regenerates this file, so your edits are safe.");
        writer.BlankLine();

        // IParsable and the converter take nullable parameters, and the consumer's project may not
        // have nullable reference types on (CS8632 on every one of them). The file says so itself
        // rather than depending on a setting it cannot see.
        writer.WriteLine("#nullable enable");
        writer.BlankLine();
        writer.WriteLine("using System;");
        writer.WriteLine("using System.Text.Json;");
        writer.WriteLine("using System.Text.Json.Serialization;");
        writer.BlankLine();
        writer.WriteLine($"namespace {ns};");
        writer.BlankLine();

        foreach (var (name, inner) in ids.Declarations)
        {
            var aggregate = name[..^2];

            writer.WriteLine($"/// <summary>The identity of {aggregate}, and of everything that addresses its stream.</summary>");
            writer.WriteLine($"[JsonConverter(typeof({name}.Converter))]");
            writer.Write($"BLOCK:public readonly record struct {name}({inner} Value) : IParsable<{name}>");

            writer.Write("public override string ToString() => Value.ToString();");
            writer.BlankLine();

            writer.Write(inner == "Guid"
                ? $"public static {name} Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s));"
                : $"public static {name} Parse(string s, IFormatProvider? provider) => new(s);");
            writer.BlankLine();

            writer.Write($"BLOCK:public static bool TryParse(string? s, IFormatProvider? provider, out {name} result)");
            if (inner == "Guid")
            {
                writer.Write("var parsed = Guid.TryParse(s, out var value);");
                writer.Write($"result = new {name}(value);");
                writer.Write("return parsed;");
            }
            else
            {
                writer.Write($"result = new {name}(s ?? string.Empty);");
                writer.Write("return s is not null;");
            }
            writer.FinishBlock();
            writer.BlankLine();

            writer.WriteLine("/// <summary>So the wire carries the value, not an object wrapping it.</summary>");
            writer.Write($"BLOCK:public sealed class Converter : JsonConverter<{name}>");
            writer.Write($"public override {name} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
            writer.IndentionLevel++;
            writer.Write(inner == "Guid" ? "=> new(reader.GetGuid());" : "=> new(reader.GetString() ?? string.Empty);");
            writer.IndentionLevel--;
            writer.BlankLine();
            writer.Write($"public override void Write(Utf8JsonWriter writer, {name} value, JsonSerializerOptions options)");
            writer.IndentionLevel++;
            writer.Write("=> writer.WriteStringValue(value.Value);");
            writer.IndentionLevel--;
            writer.FinishBlock();

            writer.FinishBlock();
            writer.BlankLine();
        }

        return new GeneratedFile("Identities.cs", writer.Code());
    }

    /// <summary>
    /// The <c>EventModelDefinition</c> subclass's name.
    /// </summary>
    /// <remarks>
    /// <b>It must not equal the namespace</b>, which is the default it would otherwise take: the
    /// model name supplies both, so <c>namespace K9Crush { class K9Crush }</c> came out of the
    /// first cut. That compiles, and then every reference to a sibling stub inside it resolves
    /// against the CLASS before the namespace — so <c>K9Crush.SwipeOnDog</c> stops meaning what it
    /// says and the error names a type nobody wrote. An <c>EventModel</c> suffix, only where the
    /// collision is real, keeps the ordinary case reading as the model's own name.
    /// </remarks>
    public static string DefinitionClassName(string modelName, string namespaceName)
    {
        var name = Identifiers.Sanitize(modelName);
        if (name.Length == 0) return "ImportedEventModel";

        var last = namespaceName.Split('.').LastOrDefault() ?? "";

        return string.Equals(name, last, StringComparison.Ordinal) ? name + "EventModel" : name;
    }

    private static string writeDefinition(string ns, ImportedEventModel model, IReadOnlyList<string> stubs, ModelLayout layout,
        StrongTypedIds ids)
    {
        var className = DefinitionClassName(model.Model, ns);

        var stubbed = stubs.ToHashSet(StringComparer.Ordinal);

        using var writer = new SourceWriter();

        writer.WriteLine("// Imported from an eventmodelers.ai board by `bobcat import-event-model`.");
        writer.WriteLine("//");
        writer.WriteLine("// This is ordinary C# and nothing regenerates it. The segmentation an import performs is a");
        writer.WriteLine("// set of reported guesses, so a wrong guess is a one-line edit here rather than a re-import.");
        writer.BlankLine();
        writer.WriteLine("using JasperFx.Events.EventModeling;");
        foreach (var chapter in layout.Namespaces.Where(x => x != ns)) writer.WriteLine($"using {chapter};");
        writer.BlankLine();
        writer.WriteLine($"namespace {ns};");
        writer.BlankLine();

        writer.Write($"BLOCK:public class {className} : EventModelDefinition");

        writer.Write($"public override string Name => `{escape(model.Model)}`;");
        writer.BlankLine();

        writer.Write("BLOCK:public override void Configure(EventModelBuilder model)");

        var aggregates = model.Slices
            .SelectMany(AggregatesOf)
            .Select(x => Identifiers.Sanitize(x ?? ""))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (aggregates.Count > 0)
        {
            foreach (var aggregate in aggregates) writer.Write(declare("model.Aggregate", aggregate, stubbed) + identifiedBy(aggregate, ids) + ";");
            writer.BlankLine();
        }

        foreach (var slice in model.Slices)
        {
            writeSlice(writer, slice, stubbed);
            writer.BlankLine();
        }

        writer.FinishBlock();
        writer.FinishBlock();

        return writer.Code();
    }

    /// <summary>What a per-chapter definition already says at the top, so its slices do not repeat it.</summary>
    private sealed record SliceDefaults(bool Chapter, bool Domain, string? Aggregate);

    /// <summary>
    /// bobcat#448: one definition per chapter. The chapter, a domain every slice shares, and the
    /// aggregate most of its commands decide against are said once, at the top; a slice that agrees
    /// with the default drops its own <c>.Against</c>, one that does not keeps it.
    /// </summary>
    private static IReadOnlyList<GeneratedFile> writeChapterDefinitions(string ns, ImportedEventModel model, IReadOnlyList<string> stubs,
        ModelLayout layout, StrongTypedIds ids)
    {
        var stubbed = stubs.ToHashSet(StringComparer.Ordinal);
        var files = new List<GeneratedFile>();
        var declared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chapter in model.Slices.GroupBy(x => ModelLayout.ChapterFolder(x.Chapter)))
        {
            var slices = chapter.ToList();
            var folder = chapter.Key;
            var chapterNs = ModelLayout.NamespaceFor(ns, folder);
            var className = folder is null ? DefinitionClassName(model.Model, ns) : folder + "Model";
            if (stubbed.Contains(className)) className = (folder ?? Identifiers.Sanitize(model.Model)) + "EventModel";

            var domains = slices.Select(x => x.Domain).Distinct(StringComparer.Ordinal).ToList();
            var sharedDomain = domains.Count == 1 && domains[0] is { Length: > 0 } ? domains[0] : null;
            var (dominant, agreeing, deciding, inferred) = dominantAggregate(slices);

            using var writer = new SourceWriter();
            writer.WriteLine("// Imported from an eventmodelers.ai board by `bobcat import-event-model`.");
            writer.WriteLine("//");
            writer.WriteLine("// This is ordinary C# and nothing regenerates it. The segmentation an import performs is a");
            writer.WriteLine("// set of reported guesses, so a wrong guess is a one-line edit here rather than a re-import.");
            writer.BlankLine();
            writer.WriteLine("using JasperFx.Events.EventModeling;");
            foreach (var other in layout.Namespaces.Where(x => x != chapterNs)) writer.WriteLine($"using {other};");
            writer.BlankLine();
            writer.WriteLine($"namespace {chapterNs};");
            writer.BlankLine();

            // No Name: the definition joins the application's model, as every chapter's does (jasperfx#992)
            writer.Write($"BLOCK:public class {className} : EventModelDefinition");
            writer.Write("BLOCK:public override void Configure(EventModelBuilder model)");

            if (slices[0].Chapter is { Length: > 0 } chapterName) writer.Write($"model.InChapter(`{escape(chapterName)}`);");
            if (sharedDomain is not null) writer.Write($"model.InDomain(`{escape(sharedDomain)}`);");

            if (dominant is not null)
            {
                if (inferred)
                {
                    writer.WriteLine($"// ⚠ inferred: {agreeing} of the {deciding} commands here decide against {dominant}, so it is the");
                    writer.WriteLine("// default; .Against<T>() on a slice replaces it, and .NoAggregate() says it has none.");
                }

                writer.Write(declare("model.ForAggregate", dominant, stubbed) + identifiedBy(dominant, ids) + ";");
                declared.Add(dominant);
            }

            // An aggregate is declared in the first chapter that uses it; ForAggregate declares its own
            var aggregates = slices.SelectMany(AggregatesOf).Select(x => Identifiers.Sanitize(x)).Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal).Where(declared.Add).ToList();
            foreach (var aggregate in aggregates) writer.Write(declare("model.Aggregate", aggregate, stubbed) + identifiedBy(aggregate, ids) + ";");
            writer.BlankLine();

            var defaults = new SliceDefaults(Chapter: true, Domain: sharedDomain is not null, Aggregate: dominant);
            foreach (var slice in slices)
            {
                writeSlice(writer, slice, stubbed, defaults);
                writer.BlankLine();
            }

            writer.FinishBlock();
            writer.FinishBlock();

            var path = folder is null ? $"{className}.cs" : $"{ModelLayout.FeaturesFolder}/{folder}/{className}.cs";
            files.Add(new GeneratedFile(path, writer.Code()));
        }

        return files;
    }

    /// <summary>
    /// The aggregate more than half of a chapter's deciding commands — those against an aggregate,
    /// not starting a stream — decide against alone, and at least two; null when none does.
    /// </summary>
    private static (string? Aggregate, int Agreeing, int Deciding, bool Inferred) dominantAggregate(IReadOnlyList<CuratedSlice> slices)
    {
        var deciding = slices
            .Where(x => isCommand(x) && x.StartsStream is not { Length: > 0 } && sanitized(x.Aggregates).Any())
            .ToList();

        var top = deciding
            .Select(x => (Slice: x, Aggregates: sanitized(x.Aggregates).ToList()))
            .Where(x => x.Aggregates.Count == 1)
            .GroupBy(x => x.Aggregates[0], StringComparer.Ordinal)
            .OrderByDescending(x => x.Count())
            .FirstOrDefault();

        if (top is null || top.Count() < 2 || top.Count() * 2 <= deciding.Count) return (null, 0, deciding.Count, false);

        var inferred = top.Any(x => x.Slice.Inferred.Keys.Any(k => Identifiers.Sanitize(k) == top.Key));
        return (top.Key, top.Count(), deciding.Count, inferred);
    }

    private static bool isCommand(CuratedSlice slice) => (slice.Pattern ?? "").Trim().Equals("command", StringComparison.OrdinalIgnoreCase);

    // The slice decides against exactly the default aggregate, so ForAggregate already says it
    private static bool takesDefault(CuratedSlice slice, SliceDefaults? defaults)
        => defaults?.Aggregate is { } aggregate && isCommand(slice) && slice.StartsStream is not { Length: > 0 }
           && sanitized(slice.Aggregates).ToList() is [var only] && only == aggregate;

    private static void writeSlice(ISourceWriter writer, CuratedSlice slice, HashSet<string> stubbed)
        => writeSlice(writer, slice, stubbed, null);

    /// <summary>
    /// One slice's declaration, as the statement a definition's <c>Configure</c> holds, for the additive
    /// re-import (<see cref="ImportMerge"/>): written against what the EXISTING definition already says at
    /// its top, not what a fresh import would, so a slice added to a file the user corrected agrees with it.
    /// </summary>
    /// <param name="slice">The slice to declare.</param>
    /// <param name="stubbed">The types that exist, so a role is generic only over a type that compiles.</param>
    /// <param name="chapterSaid">The chapter the definition's <c>model.InChapter</c> names, if any.</param>
    /// <param name="domainSaid">The domain its <c>model.InDomain</c> names, if any.</param>
    /// <param name="aggregateDefault">The aggregate its <c>model.ForAggregate</c> names, if any.</param>
    internal static string SliceDeclaration(CuratedSlice slice, IReadOnlyCollection<string> stubbed, string? chapterSaid, string? domainSaid,
        string? aggregateDefault)
    {
        var defaults = new SliceDefaults(
            Chapter: chapterSaid is not null && string.Equals(chapterSaid, slice.Chapter, StringComparison.Ordinal),
            Domain: domainSaid is not null && string.Equals(domainSaid, slice.Domain, StringComparison.Ordinal),
            Aggregate: aggregateDefault);

        using var writer = new SourceWriter();
        writeSlice(writer, slice, stubbed.ToHashSet(StringComparer.Ordinal), defaults);
        return writer.Code();
    }

    /// <summary>The declaration of an aggregate, generic when its type exists.</summary>
    internal static string AggregateDeclaration(string aggregate, IReadOnlyCollection<string> stubbed)
        => AggregateDeclaration(aggregate, stubbed, StrongTypedIds.None);

    /// <inheritdoc cref="AggregateDeclaration(string, IReadOnlyCollection{string})"/>
    internal static string AggregateDeclaration(string aggregate, IReadOnlyCollection<string> stubbed, StrongTypedIds ids)
        => (declare("model.Aggregate", aggregate, stubbed.ToHashSet(StringComparer.Ordinal)) + identifiedBy(aggregate, ids))
            .Replace('`', '"') + ";";

    private static void writeSlice(ISourceWriter writer, CuratedSlice slice, HashSet<string> stubbed, SliceDefaults? defaults)
    {
        var name = Identifiers.Sanitize(slice.Name);
        if (name.Length == 0) name = "Unnamed";

        if (slice.Notes is { Length: > 0 } notes)
        {
            foreach (var line in notes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                writer.WriteLine($"// {line.TrimEnd()}");
        }

        // bobcat#444: every inferred, missing or several-stream aggregate is said where it is used
        foreach (var callout in slice.Callouts) writer.WriteLine($"// {callout}");

        // A command the import found no aggregate for still takes the chapter's default, so say so
        if (defaults?.Aggregate is { } fallback && isCommand(slice) && slice.StartsStream is not { Length: > 0 }
            && !sanitized(slice.Aggregates).Any())
        {
            writer.WriteLine($"// ⚠ no aggregate of its own, so it takes the chapter's default, {fallback}: say .Against<T>() or .NoAggregate() if not.");
        }

        // The pattern verb opens the slice, so the descriptor's Pattern is a fact of the call
        // rather than a separate statement that could disagree with it. A slice whose pattern the
        // board did not say falls back to Slice(name) — Pattern stays null, and a null pattern
        // leaves the canvas uncoloured where a wrong one would miscolour it.
        //
        // A GENERIC verb takes its slice name from the type, so it is only usable when that name
        // is the one the board gave the slice. There is no call that renames a slice afterwards,
        // and the name IS the merge key — so getting this wrong would silently split one slice
        // into two rather than failing. When they differ, the slice opens by name and states its
        // pattern and its role separately, which says exactly the same thing.
        var pattern = (slice.Pattern ?? "").Trim().ToLowerInvariant();
        var command = slice.Command is { Length: > 0 } ? Identifiers.Sanitize(slice.Command) : null;
        var view = sanitized(slice.ReadModels).FirstOrDefault();

        string opener;
        string? namedByOpener = null;

        switch (pattern)
        {
            case "command" when command is { Length: > 0 } && command == name && stubbed.Contains(command):
                opener = $"model.Command<{command}>()";
                namedByOpener = command;
                break;

            case "command":
                opener = $"model.Command(`{escape(name)}`)";
                break;

            case "view" when view is { Length: > 0 } && view == name && stubbed.Contains(view):
                opener = $"model.View<{view}>()";
                namedByOpener = view;
                break;

            case "view":
                opener = $"model.View(`{escape(name)}`)";
                break;

            case "automation":
                opener = $"model.Automation(`{escape(name)}`)";
                break;

            case "translation":
                opener = $"model.Translation(`{escape(name)}`)";
                break;

            default:
                opener = $"model.Slice(`{escape(name)}`)";
                break;
        }

        var calls = new List<string>();

        // A Slice(name) opener carries no pattern, so say it outright when the board knew one.
        // Only for the four the enum has words for: anything else the board might say is not a
        // SlicePattern, and forcing it into the nearest member would miscolour the canvas.
        if (opener.StartsWith("model.Slice(", StringComparison.Ordinal)
            && slicePattern(slice.Pattern) is { } declaredPattern)
        {
            calls.Add($".Pattern(SlicePattern.{declaredPattern})");
        }

        if (defaults?.Domain != true && slice.Domain is { Length: > 0 } domain) calls.Add($".InDomain(`{escape(domain)}`)");
        if (defaults?.Chapter != true && slice.Chapter is { Length: > 0 } chapter) calls.Add($".InChapter(`{escape(chapter)}`)");

        if (slice.Trigger is { } trigger)
        {
            var kind = triggerKind(trigger.Kind);

            if (trigger.Label is { Length: > 0 } label)
            {
                calls.Add(kind is null
                    ? $".TriggeredBy(`{escape(label)}`)"
                    : $".TriggeredBy(`{escape(label)}`, TriggerKind.{kind})");
            }
            else if (kind is not null)
            {
                calls.Add($".TriggeredBy(TriggerKind.{kind})");
            }
        }

        // Stated separately unless the opener already carried it — which is the whole reason
        // namedByOpener exists rather than sniffing the emitted string for a substring.
        if (command is { Length: > 0 } && command != namedByOpener)
        {
            calls.Add(role(".Command", command, stubbed));
        }

        if (slice.Handler is { Length: > 0 } handler)
        {
            // Always by name: a handler is never stubbed (an empty handler is what the scaffold
            // command exists to write), so there is no type to be generic over.
            calls.Add($".HandledBy(`{escape(Identifiers.Sanitize(handler))}`)");
        }

        if (slice.StartsStream is { Length: > 0 } started) calls.Add(role(".StartsStream", Identifiers.Sanitize(started), stubbed));
        if (!takesDefault(slice, defaults))
        {
            foreach (var aggregate in sanitized(slice.Aggregates)) calls.Add(role(".Against", aggregate, stubbed));
        }
        foreach (var @event in sanitized(slice.Events)) calls.Add(role(".Emits", @event, stubbed));
        foreach (var message in sanitized(slice.Messages)) calls.Add(role(".Publishes", message, stubbed));
        foreach (var consumed in sanitized(slice.ConsumedEvents)) calls.Add(role(".On", consumed, stubbed));
        foreach (var read in sanitized(slice.ReadsFrom)) calls.Add(role(".Reads", read, stubbed));
        foreach (var projection in sanitized(slice.Projections))
            calls.Add($".Projects(`{escape(projection)}`)");

        foreach (var readModel in sanitized(slice.ReadModels))
        {
            if (readModel == namedByOpener) continue;
            calls.Add(role(".Produces", readModel, stubbed));
        }

        foreach (var external in slice.ExternalSystems)
        {
            if (string.IsNullOrWhiteSpace(external.Name)) continue;

            var direction = (external.Direction ?? "").Trim().Equals("Outbound", StringComparison.OrdinalIgnoreCase)
                ? "Outbound"
                : "Inbound";

            // No endpoint URI: the curated shape carries only a name and a direction, which is
            // all an emlang board names. The fluent overload's third argument is left to whoever
            // edits this file and knows the endpoint.
            calls.Add($".ExternalSystem(`{escape(external.Name)}`, ExternalSystemDirection.{direction})");
        }

        // Spec identities are {Feature}/{Scenario}, and the feature half defaults to the slice
        // name — the same rule the curated shape documents, so a link here is the same string a
        // run's scenario_finished will carry and the two join with no mapping table.
        if (slice.Specifications is { } specifications)
        {
            var feature = specifications.Feature is { Length: > 0 } declared ? declared : slice.Name;

            foreach (var scenario in specifications.Scenarios)
            {
                if (scenario.Name is { Length: > 0 })
                    calls.Add($".LinksToSpecification(`{escape(feature)}/{escape(scenario.Name)}`)");
            }
        }

        foreach (var hotspot in slice.Hotspots)
        {
            if (hotspot is { Length: > 0 }) calls.Add($".Hotspot(`{escape(hotspot)}`)");
        }

        if (calls.Count == 0)
        {
            writer.Write($"{opener};");
            return;
        }

        // One call per line, indented under the opener — the shape the issue's own example uses,
        // and the one a reviewer can read as a list of claims.
        writer.Write(opener);
        writer.IndentionLevel++;
        for (var i = 0; i < calls.Count; i++)
        {
            writer.Write(i == calls.Count - 1 ? calls[i] + ";" : calls[i]);
        }

        writer.IndentionLevel--;
    }

    private static IEnumerable<string> sanitized(IEnumerable<string>? values)
        => (values ?? []).Select(x => Identifiers.Sanitize(x ?? "")).Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// A role call: generic when this writer emitted the stub (so the type is known to compile),
    /// by name otherwise.
    /// </summary>
    /// <remarks>
    /// The two are equivalent to the merge — a string becomes a <c>TypeDescriptor(name, name, "")</c>
    /// whose empty assembly makes comparison fall back to <c>Name</c> (jasperfx#798) — so this is
    /// about the generated file compiling, not about what it means. Reaching for
    /// <c>&lt;Something&gt;</c> that no stub defines would make the import's output not build,
    /// which is the one thing a scaffolded file must never do.
    /// </remarks>
    private static string role(string method, string name, HashSet<string> stubbed)
        => stubbed.Contains(name) ? $"{method}<{name}>()" : $"{method}(`{escape(name)}`)";

    private static string declare(string method, string name, HashSet<string> stubbed)
        => stubbed.Contains(name) ? $"{method}<{name}>()" : $"{method}(`{escape(name)}`)";

    /// <summary>
    /// The aggregate's declared identity type (bobcat#512, jasperfx#1004), or nothing when it keeps a
    /// primitive one. Generic: the struct is written in the same import, so the type is known to compile
    /// — and a <c>Declared</c> claim the store's own projection later agrees with is how the two rungs
    /// of the model meet on an identity rather than minting a hotspot.
    /// </summary>
    private static string identifiedBy(string aggregate, StrongTypedIds ids)
        => ids.IdOf(aggregate) is { } id ? $".IdentifiedBy<{id}>()" : "";

    /// <summary>
    /// The board's pattern word as a <c>SlicePattern</c> member, or null for anything the enum has
    /// no word for — in which case the slice carries no pattern at all, because a null pattern
    /// leaves the canvas uncoloured where a wrong one miscolours it.
    /// </summary>
    private static string? slicePattern(string? pattern)
        => (pattern ?? "").Trim().ToLowerInvariant() switch
        {
            "command" => "Command",
            "view" => "View",
            "automation" => "Automation",
            "translation" => "Translation",
            _ => null
        };

    /// <summary>
    /// The board's trigger word as a <c>TriggerKind</c> member, or null when it says nothing this
    /// enum has a word for — in which case the label travels alone rather than being forced into
    /// the nearest member.
    /// </summary>
    private static string? triggerKind(string? kind)
        => (kind ?? "").Trim().ToLowerInvariant() switch
        {
            "http" => "Http",
            "grpc" => "Grpc",
            "messagehandler" or "message" => "MessageHandler",
            "jobscheduler" or "scheduler" or "job" => "JobScheduler",
            "human" => "Human",
            "external" => "External",
            _ => null
        };

    /// <summary>
    /// Escape a string for a C# literal written with backticks. The backtick is
    /// <see cref="ISourceWriter.Write"/>'s stand-in for a double quote, so a literal backtick in
    /// the board's own text would silently become one — hence the swap to a single quote, which is
    /// what a person transcribing a quoted phrase into prose would do anyway.
    /// </summary>
    private static string escape(string value)
        => value.Replace("\\", "\\\\").Replace("`", "'").Replace("\r", "").Replace("\n", " ");

    /// <summary>
    /// Board labels into C# identifiers. Shared with the stub writer so a name can never be
    /// sanitized one way in a record declaration and another way in the call that references it.
    /// </summary>
    public static class Identifiers
    {
        public static string Sanitize(string value)
        {
            var builder = new StringBuilder(value.Length);

            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character) || character == '_') builder.Append(character);
            }

            var name = builder.ToString();
            if (name.Length == 0) return name;

            // A C# identifier cannot start with a digit, and a board label like "2FA enrolled"
            // would otherwise produce a record declaration that does not parse.
            if (char.IsDigit(name[0])) name = "_" + name;

            return char.IsLower(name[0]) ? char.ToUpperInvariant(name[0]) + name[1..] : name;
        }
    }
}
