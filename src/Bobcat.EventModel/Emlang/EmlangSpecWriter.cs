using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JasperFx.CodeGeneration;

namespace Bobcat.EventModel.Emlang;

/// <summary>What <see cref="EmlangSpecWriter.Write"/> produced.</summary>
/// <param name="Code">The specifications file.</param>
/// <param name="Features">How many feature classes it holds: one per slice with an example.</param>
/// <param name="Specs">How many specifications: one per test with something to say.</param>
/// <param name="Additions">What the stubs need beyond the model's own for the specs to compile.</param>
/// <param name="Report">Every guess and gap, one line each, for the person to go and fix.</param>
public sealed record GeneratedSpecs(
    string Code,
    int Features,
    int Specs,
    CSharpModelWriter.StubAdditions Additions,
    IReadOnlyList<string> Report);

/// <summary>
/// Writes an emlang model's examples as WolverineFx.Bobcat specifications (bobcat#423): one
/// <c>[BobcatFeature]</c> class per slice, one <c>[Fact]</c> per test, each value the example
/// gives as a partial object naming exactly those members.
/// </summary>
/// <remarks>
/// <para>
/// <b>A specification's identity is the one the model links (bobcat#435).</b> The feature is the
/// SLICE the import attached the test to, not the board chapter it was written under, and the
/// scenario is <see cref="ScenarioTitle"/>, the method name read back as a sentence, which is what
/// a projected test reports. <c>EmlangImport</c> names each scenario the same way, so the
/// definition's <c>LinksToSpecification</c>, the pushed descriptor and a run's
/// <c>scenario_finished</c> all carry one string. Before this, the features were chapters and
/// the definition linked the board's spelling, so not one generated specification joined its slice.
/// </para>
/// <para>
/// <b>The code targets WolverineFx.Bobcat's <c>WolverineSpec</c></b>, which this package does not
/// reference: it is text, and the project it lands in references WolverineFx.Bobcat.
/// </para>
/// <para>
/// <b>Types come from the stubs.</b> Every member's type is what
/// <see cref="CSharpModelWriter.FieldsOf"/> gives the stub, so the generated specs and the stubs
/// written beside them always agree. An identity the model does not type is a Guid, declared as a
/// local (<c>var theOrder = Guid.NewGuid();</c>) so the rendered spec names it <c>theOrder</c>.
/// </para>
/// <para>
/// <b>It is one-shot.</b> The command writes the file only when it does not exist; run again,
/// <see cref="MissingSpecs"/> reports the model's tests that have no specification.
/// </para>
/// </remarks>
public static class EmlangSpecWriter
{
    public static GeneratedSpecs Write(EmlangBoard board, ImportedEventModel model, string ns)
    {
        var context = new Context(model);
        context.Declare(board);
        var modelName = CSharpModelWriter.Identifiers.Sanitize(model.Model);
        if (modelName.Length == 0) modelName = "Imported";

        using var writer = new SourceWriter();
        writer.WriteLine("// Generated from an event model by `bobcat import-event-model --specs`.");
        writer.WriteLine("//");
        writer.WriteLine("// One specification per example in the model, each value a partial object that names only");
        writer.WriteLine("// what the example names. Nothing regenerates this file: run the import again and it reports");
        writer.WriteLine("// the model's examples that have no specification here, rather than overwriting your edits.");
        writer.BlankLine();
        writer.WriteLine("using System;");
        writer.WriteLine("using System.Collections.Generic;");
        writer.WriteLine("using System.Threading.Tasks;");
        writer.WriteLine("using Bobcat;");
        writer.WriteLine("using Microsoft.Extensions.Hosting;");
        writer.WriteLine("using Wolverine;");
        writer.WriteLine("using Wolverine.Bobcat;");
        writer.WriteLine("using Xunit;");
        writer.BlankLine();
        writer.WriteLine($"namespace {ns};");
        writer.BlankLine();

        writeFixture(writer, modelName);

        var features = 0;
        var specs = 0;
        var classNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var slice in model.Slices)
        {
            var scenarios = slice.Specifications?.Scenarios.Where(x => x.Source is not null).ToList() ?? [];
            if (scenarios.Count == 0)
            {
                context.Report.Add($"slice '{slice.Name}': no tests, so no specifications.");
                continue;
            }

            // A slice and one of its examples often share a name (a view slice and the test of that
            // view), and C# refuses a member named for its enclosing type (CS0542). The class name is
            // not part of the identity, [BobcatFeature] is, so the class gives way, never the method.
            var methodNames = scenarios.Select(x => MethodName(x.Source!.Name)).ToHashSet(StringComparer.Ordinal);
            var classCandidate = FeatureClassName(slice.Name);
            if (methodNames.Contains(classCandidate)) classCandidate += "_feature";
            var className = unique(classNames, classCandidate);
            features++;

            writer.BlankLine();
            if (slice.Pattern == "Automation" && slice.Trigger?.Label is { Length: > 0 } trigger)
            {
                // The examples exercise the command; what triggers it is wiring they never reach
                writer.WriteLine($"// {slice.Name} is an automation, triggered by \"{comment(trigger)}\"");
            }

            writer.WriteLine($"[BobcatFeature({quote(slice.Name)})]");
            writer.Write($"BLOCK:public class {className}(AppFixture app) : {modelName}Spec(app)");

            var first = true;
            foreach (var scenario in scenarios)
            {
                if (!first) writer.BlankLine();
                first = false;

                // The import already deduplicated the slice's scenarios by title, and a title
                // round-trips to exactly one method name, so no method here needs a suffix
                writer.WriteLine("[Fact]");
                writer.Write($"BLOCK:public async Task {MethodName(scenario.Source!.Name)}()");
                foreach (var line in new TestWriter(context, slice.Name, scenario.SourceChapter!, scenario.Source).Lines())
                {
                    if (line.Length == 0) writer.BlankLine();
                    else writer.WriteLine(line);
                }
                writer.FinishBlock();
                specs++;
            }

            writer.FinishBlock();
        }

        // A test the import attached to no slice still gets its specification, under its chapter as
        // before: what it arranges and asserts is still worth writing, and its examples are still
        // fields the stubs need. It binds to nothing on the model, and the report says so.
        var attached = model.Slices
            .SelectMany(x => x.Specifications?.Scenarios ?? [])
            .Select(x => x.Source)
            .OfType<EmlangTest>()
            .ToHashSet(ReferenceEqualityComparer.Instance);

        foreach (var chapter in board.Chapters)
        {
            var orphans = chapter.Tests.Where(x => !attached.Contains(x)).ToList();
            if (orphans.Count == 0) continue;

            var className = unique(classNames, FeatureClassName(chapter.Name));
            features++;

            writer.BlankLine();
            writer.WriteLine($"// The model attaches these to no slice, so they bind to nothing on the event model");
            writer.WriteLine($"[BobcatFeature({quote(chapter.Name)})]");
            writer.Write($"BLOCK:public class {className}(AppFixture app) : {modelName}Spec(app)");

            var methods = new HashSet<string>(StringComparer.Ordinal);
            var first = true;
            foreach (var test in orphans)
            {
                if (!first) writer.BlankLine();
                first = false;

                context.Report.Add($"⚠ chapter '{chapter.Name}', test '{test.Name}': attached to no slice, so its specification binds to nothing on the model.");
                writer.WriteLine("[Fact]");
                writer.Write($"BLOCK:public async Task {unique(methods, MethodName(test.Name))}()");
                foreach (var line in new TestWriter(context, chapter.Name, chapter, test).Lines())
                {
                    if (line.Length == 0) writer.BlankLine();
                    else writer.WriteLine(line);
                }
                writer.FinishBlock();
                specs++;
            }

            writer.FinishBlock();
        }

        return new GeneratedSpecs(writer.Code(), features, specs,
            new CSharpModelWriter.StubAdditions(context.Streams.ToList(), context.Documents.ToList(), context.Elements.ToList(),
                context.ExtraFields.ToDictionary(x => x.Key, x => (IReadOnlyList<CSharpModelWriter.StubField>)x.Value)),
            context.Report);
    }

    /// <summary>
    /// The model's scenarios with no specification among <paramref name="sources"/>, as
    /// <c>slice / scenario</c>: what a second run reports instead of writing over the first.
    /// </summary>
    public static IReadOnlyList<string> MissingSpecs(ImportedEventModel model, IEnumerable<string> sources)
    {
        var written = new HashSet<(string Feature, string Method)>();
        foreach (var source in sources)
        {
            string? feature = null;
            foreach (var line in source.Split('\n'))
            {
                if (FeatureAttribute.Match(line) is { Success: true } attribute)
                {
                    feature = Regex.Unescape(attribute.Groups[1].Value);
                }
                else if (feature is not null && TestMethod.Match(line) is { Success: true } method)
                {
                    written.Add((feature, method.Groups[1].Value));
                }
            }
        }

        return model.Slices
            .SelectMany(slice => (slice.Specifications?.Scenarios ?? [])
                .Where(x => x.Source is not null && !written.Contains((slice.Name, MethodName(x.Source.Name))))
                .Select(x => $"{slice.Name} / {x.Name}"))
            .ToList();
    }

    private static readonly Regex FeatureAttribute = new(@"\[BobcatFeature\(""((?:[^""\\]|\\.)*)""\)\]", RegexOptions.Compiled);
    private static readonly Regex TestMethod = new(@"public\s+async\s+Task\s+(\w+)\s*\(", RegexOptions.Compiled);

    /// <summary>A slice's class name: snake case, as Bobcat feature classes are written.</summary>
    public static string FeatureClassName(string slice)
    {
        var name = snake(slice);

        // A single lowercase word is reserved for future keywords (CS8981)
        return name.Contains('_') ? name : name + "_slice";
    }

    /// <summary>A test's method name: snake case.</summary>
    public static string MethodName(string test) => snake(test);

    /// <summary>
    /// The scenario title a test's generated specification reports: its <see cref="MethodName"/>
    /// read back as a sentence, the way the projected lane titles every test (bobcat#435).
    /// <c>EmlangImport</c> names the model's scenario this, so the two always agree.
    /// </summary>
    public static string ScenarioTitle(string test) => ProjectedSpecNaming.ScenarioTitleFor(MethodName(test));

    private static void writeFixture(ISourceWriter writer, string modelName)
    {
        writer.WriteLine("/// <summary>");
        writer.WriteLine("/// The application under test. TODO: start the application's own host here, configured as it is");
        writer.WriteLine("/// in production, with Marten and Wolverine. This placeholder only compiles.");
        writer.WriteLine("/// </summary>");
        writer.Write("BLOCK:public class AppFixture : IAsyncLifetime");
        writer.WriteLine("public IHost Host { get; private set; } = null!;");
        writer.BlankLine();
        writer.WriteLine("public async ValueTask InitializeAsync()");
        writer.WriteLine("    => Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().UseWolverine().StartAsync();");
        writer.BlankLine();
        writer.Write("BLOCK:public async ValueTask DisposeAsync()");
        writer.WriteLine("await Host.StopAsync();");
        writer.WriteLine("Host.Dispose();");
        writer.FinishBlock();
        writer.FinishBlock();
        writer.BlankLine();
        writer.WriteLine($"[CollectionDefinition({quote(modelName)})]");
        writer.WriteLine($"public class {modelName}Collection : ICollectionFixture<AppFixture>;");
        writer.BlankLine();
        writer.WriteLine($"[Collection({quote(modelName)})]");
        writer.WriteLine($"public abstract class {modelName}Spec(AppFixture app) : WolverineSpec(app.Host);");
    }

    private sealed class Context(ImportedEventModel model)
    {
        private readonly Dictionary<string, (string? Stream, List<string> Identities, List<string> Props)> _declared = new(StringComparer.Ordinal);

        /// <summary>
        /// What the model's own steps say about each element: its stream, and which props are its
        /// identity. A scenario item often repeats neither: an eventmodelers.ai export names the
        /// aggregate on the element, not on every example of it (bobcat#433 discussion, case C).
        /// </summary>
        public void Declare(EmlangBoard board)
        {
            foreach (var step in board.Chapters.SelectMany(x => x.Steps))
            {
                if (step.Kind is not (EmlangElementKind.Command or EmlangElementKind.Event or EmlangElementKind.View)) continue;

                var typeName = TypeName(step.Label);
                var stream = step.Stream is { Length: > 0 } declared ? declared
                    : step.Kind == EmlangElementKind.Event && step.Actor.Length > 0 ? step.Actor
                    : null;

                if (!_declared.TryGetValue(typeName, out var known))
                {
                    _declared[typeName] = (stream, [.. step.Identities], [.. step.Props.Keys]);
                    continue;
                }

                if (known.Stream is null && stream is not null) _declared[typeName] = known with { Stream = stream };
                foreach (var identity in step.Identities.Where(x => !known.Identities.Contains(x))) known.Identities.Add(identity);
                foreach (var prop in step.Props.Keys.Where(x => !known.Props.Contains(x))) known.Props.Add(prop);
            }
        }

        /// <summary>The stream the model declares for an element, or null.</summary>
        public string? StreamOf(string typeName) => _declared.GetValueOrDefault(typeName).Stream;

        /// <summary>
        /// The props that are an element's identity: the ones the model marks (<c>idAttribute</c>),
        /// or, when it marks none, the prop named for its stream (<c>orderId</c> on an <c>Order</c>
        /// event) or the generic <c>aggregateId</c> some models use.
        /// </summary>
        public IReadOnlyList<string> IdentitiesOf(string typeName)
        {
            if (!_declared.TryGetValue(typeName, out var known)) return [];
            if (known.Identities.Count > 0) return known.Identities;

            var stream = known.Stream is { Length: > 0 } declared ? TypeName(declared) : null;
            return known.Props
                .Where(x => EmlangImport.PascalName(x) is var name
                            && (name == "AggregateId" || (stream is not null && name == stream + "Id")))
                .Take(1)
                .ToList();
        }

        private readonly Dictionary<string, IReadOnlyList<CSharpModelWriter.StubField>> _fields = new(StringComparer.Ordinal);

        public ImportedEventModel Model { get; } = model;
        public List<string> Report { get; } = [];
        public SortedSet<string> Streams { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Documents { get; } = new(StringComparer.Ordinal);

        /// <summary>Every command, event and view a specification names, in the order first named.</summary>
        public List<string> Elements { get; } = [];

        /// <summary>
        /// Fields an example names that the model's hints never reached, such as a test that attaches
        /// to no slice: added to the stub so the specification that names them compiles.
        /// </summary>
        public Dictionary<string, List<CSharpModelWriter.StubField>> ExtraFields { get; } = new(StringComparer.Ordinal);

        /// <summary>The member a prop names on a stub, and its type, exactly as the stub declares it.</summary>
        /// <param name="sample">The example's value, which types a field the stub does not have yet.</param>
        public (string Name, string Type) Member(string typeName, string prop, object? sample = null)
        {
            var name = CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(prop));
            if (name == typeName) name += "Value";

            if (!_fields.TryGetValue(typeName, out var fields))
            {
                fields = CSharpModelWriter.FieldsOf(Model, typeName);
                _fields[typeName] = fields;
            }

            if (fields.FirstOrDefault(x => x.Name == name) is { } declared) return (name, declared.Type);

            if (!ExtraFields.TryGetValue(typeName, out var extra)) ExtraFields[typeName] = extra = [];
            if (extra.FirstOrDefault(x => x.Name == name) is { } added) return (name, added.Type);

            var type = name.EndsWith("Id", StringComparison.Ordinal) ? "Guid"
                : sample is List<object> ? CuratedFieldTypes.StringList
                : CuratedFieldTypes.TryInfer(EmlangReader.Text(sample), out var inferred) ? inferred
                : "string";
            extra.Add(new CSharpModelWriter.StubField(name, type));
            return (name, type);
        }
    }

    /// <summary>One test's body. A class rather than a method because a test accumulates state as it goes: its identities.</summary>
    private sealed class TestWriter(Context context, string slice, EmlangChapter chapter, EmlangTest test)
    {
        private readonly List<string> _lines = [];

        // An identity's example value ("order-123") → the local that stands for it
        private readonly Dictionary<string, (string Variable, string Type)> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);

        private static readonly EmlangElementKind[] Elements =
            [EmlangElementKind.Command, EmlangElementKind.Event, EmlangElementKind.View];

        private string where => $"slice '{slice}', test '{test.Name}'";

        public IEnumerable<string> Lines()
        {
            declareIdentities();
            if (_ids.Count > 0 || _mintedLocals.Count > 0) _lines.Add("");

            var arranged = given();
            act();
            assert(arranged);

            // A minted identity whose only element wrote no partial is referenced nowhere: drop it
            foreach (var variable in _mintedLocals)
            {
                var declaration = $"var {variable} = ";
                var used = _lines.Any(x => !x.StartsWith(declaration, StringComparison.Ordinal)
                                           && System.Text.RegularExpressions.Regex.IsMatch(x, $@"\b{variable}\b"));
                if (!used) _lines.RemoveAll(x => x.StartsWith(declaration, StringComparison.Ordinal));
            }

            if (_lines.Count > 0 && _lines[0].Length == 0) _lines.RemoveAt(0);
            while (_lines.Count > 0 && _lines[^1].Length == 0) _lines.RemoveAt(_lines.Count - 1);
            return _lines;
        }

        /// <summary>One local per identity the example names: typed ones (Guids) first, then string ids.</summary>
        private void declareIdentities()
        {
            var references = test.Given.Concat(test.When).Concat(test.Then).Where(x => Elements.Contains(x.Kind)).ToList();

            foreach (var wanted in new[] { "Guid", "string" })
            {
                foreach (var reference in references)
                {
                    var typeName = TypeName(reference.Label);
                    foreach (var (prop, value) in reference.Props)
                    {
                        var (member, type) = context.Member(typeName, prop, reference.Values.GetValueOrDefault(prop));
                        if (type != wanted || value.Length == 0 || _ids.ContainsKey(value)) continue;
                        if (type == "string" && !member.EndsWith("Id", StringComparison.Ordinal)) continue;

                        declare(member, value, type);
                    }
                }
            }

            // An example that names a stream's identity is that stream's local, for every element
            // on the stream that leaves its own identity out
            foreach (var reference in references)
            {
                var typeName = TypeName(reference.Label);
                if (streamName(reference) is not { } stream || _streams.ContainsKey(stream)) continue;

                foreach (var (prop, value) in reference.Props)
                {
                    if (!identityProp(typeName, stream, prop)) continue;
                    if (_ids.TryGetValue(value, out var id)) _streams[stream] = id.Variable;
                }
            }

            // The model marks an identity but the example gives it no value (bobcat#433 discussion,
            // case C): mint one local per stream, and set it on every element that names none
            foreach (var reference in references)
            {
                var typeName = TypeName(reference.Label);
                var stream = streamName(reference);

                foreach (var prop in context.IdentitiesOf(typeName))
                {
                    if (reference.Props.TryGetValue(prop, out var given) && given.Length > 0) continue;

                    var (member, type) = context.Member(typeName, prop);
                    var owner = stream ?? (member.EndsWith("Id", StringComparison.Ordinal) && member.Length > 2 ? member[..^2] : member);
                    if (!_streams.TryGetValue(owner, out var variable))
                    {
                        variable = mint(owner, type);
                        _streams[owner] = variable;
                    }

                    if (!_minted.TryGetValue(reference, out var fields)) _minted[reference] = fields = [];
                    if (fields.All(x => x.Member != member)) fields.Add((member, variable));
                }
            }
        }

        // A stream (aggregate) -> the local that stands for its identity in this test
        private readonly Dictionary<string, string> _streams = new(StringComparer.Ordinal);

        // An identity the model marks but the example leaves out, set on the element's partial
        private readonly Dictionary<EmlangRef, List<(string Member, string Variable)>> _minted = new(ReferenceEqualityComparer.Instance);

        /// <summary>A reference's stream: the one it names, else the one the model declares for its element.</summary>
        private string? streamName(EmlangRef reference)
        {
            if (reference.Actor.Length > 0) return TypeName(reference.Actor);
            return context.StreamOf(TypeName(reference.Label)) is { Length: > 0 } declared ? TypeName(declared) : null;
        }

        private bool identityProp(string typeName, string stream, string prop)
        {
            if (context.IdentitiesOf(typeName).Contains(prop)) return true;
            if (EmlangImport.PascalName(prop) == "AggregateId") return true;
            return CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(prop)) == stream + "Id";
        }

        private readonly List<string> _mintedLocals = [];

        /// <summary>Whether a reference has anything to put in a partial: example values, or a minted identity.</summary>
        private bool hasValues(EmlangRef reference) => reference.Props.Count > 0 || _minted.ContainsKey(reference);

        /// <summary>A local for an identity the example never gives a value.</summary>
        private string mint(string owner, string type)
        {
            var name = "the" + owner;
            var count = _names[name] = _names.GetValueOrDefault(name) + 1;
            var variable = count == 1 ? name : name + count;
            _mintedLocals.Add(variable);

            _lines.Add(type == "Guid"
                ? $"var {variable} = Guid.NewGuid();"
                : $"var {variable} = {quote(EmlangImport.PascalName(owner).ToLowerInvariant() + "-1")};");
            return variable;
        }

        private void declare(string member, string value, string type)
        {
            var stem = member.EndsWith("Id", StringComparison.Ordinal) && member.Length > 2 ? member[..^2] : member;
            var name = "the" + stem;
            var count = _names[name] = _names.GetValueOrDefault(name) + 1;
            var variable = count == 1 ? name : name + count;

            _ids[value] = (variable, type);
            _lines.Add(type == "Guid"
                ? $"var {variable} = Guid.NewGuid(); // {quote(value)} in the model"
                : $"var {variable} = {quote(value)};");
        }

        /// <returns>Whether any event was arranged.</returns>
        private bool given()
        {
            var groups = new List<(string Stream, string Key, List<string> Events)>();
            var views = new List<EmlangRef>();

            foreach (var reference in test.Given)
            {
                switch (reference.Kind)
                {
                    case EmlangElementKind.Event:
                        var stream = streamOf(reference);
                        var key = keyOf(stream, reference);
                        var group = groups.FirstOrDefault(x => x.Stream == stream && x.Key == key);
                        if (group.Events is null)
                        {
                            group = (stream, key, []);
                            groups.Add(group);
                        }

                        group.Events.Add(partial(reference));
                        break;

                    case EmlangElementKind.View:
                        // Stored directly: GivenReadModel bypasses the projection, as the model does
                        views.Add(reference);
                        break;
                }
            }

            for (var i = 0; i < groups.Count; i++)
            {
                var (stream, key, events) = groups[i];
                var verb = i == 0 ? "GivenEvents" : "GivenEventsOn";
                call($"await {verb}<{stream}>({key}, ", events, ");");
            }

            foreach (var view in views)
            {
                var typeName = document(view.Label);
                _lines.Add($"await GivenReadModel<{typeName}>({documentPartial(typeName, view)});");
            }

            if (groups.Count == 0 && test.When.Any(x => x.Kind == EmlangElementKind.Command))
            {
                // The act runs against a stream that starts empty: name it, from the first event expected
                var expected = test.Then.FirstOrDefault(x => x.Kind == EmlangElementKind.Event && streamName(x) is not null);
                if (expected is not null && streamName(expected) is { } stream
                    && (identityOf(stream, expected) ?? _streams.GetValueOrDefault(stream)) is { } key)
                {
                    context.Streams.Add(stream);
                    _lines.Add($"await GivenNoEventsFor<{stream}>({key});");
                }
            }

            if (_lines.Count > 0 && _lines[^1].Length > 0) _lines.Add("");
            return groups.Count > 0;
        }

        private void act()
        {
            var commands = test.When.Where(x => x.Kind == EmlangElementKind.Command).ToList();
            foreach (var command in commands) _lines.Add($"await WhenReceived({partial(command)});");
            if (commands.Count > 0) _lines.Add("");
        }

        private void assert(bool arranged)
        {
            var acted = test.When.Any(x => x.Kind == EmlangElementKind.Command);
            var events = test.Then.Where(x => x.Kind == EmlangElementKind.Event).ToList();
            var refusals = test.Then.Where(x => x.Kind == EmlangElementKind.Error).ToList();
            var views = test.Then.Where(x => x.Kind == EmlangElementKind.View).ToList();

            foreach (var refusal in refusals)
            {
                // A refusal's props are the values its message names: EmailAlreadyInUse { email }
                var named = refusal.Props.Values
                    .Where(x => x.Length > 0)
                    .Select(x => _ids.TryGetValue(x, out var id) ? id.Variable : quote(x));
                _lines.Add($"ThenRefusedWith({string.Join(", ", new[] { quote(refusal.Label) }.Concat(named))});");
            }

            if (events.Count > 0)
            {
                call("ThenEvents(", events.Select(partial).ToList(), ");");
            }
            else if (acted && views.Count == 0)
            {
                // A refusal appends nothing, and neither does an act the model expects nothing from
                _lines.Add("ThenNoEvents();");
            }

            foreach (var view in views)
            {
                var typeName = document(view.Label);

                // No identity in the example: a singleton view, the only one of its type
                var key = documentKey(typeName, view);
                if (key is null)
                {
                    context.Report.Add($"{where}: the {typeName} view names no identity, so it is checked as the only {typeName}.");
                    _lines.Add(!hasValues(view)
                        ? $"await ThenSingleReadModel<{typeName}>();"
                        : $"await ThenSingleReadModel<{typeName}>({partial(view)});");
                    continue;
                }

                _lines.Add(!hasValues(view)
                    ? $"await ThenReadModel<{typeName}>({key});"
                    : $"await ThenReadModel<{typeName}>({key}, {partial(view)});");
            }

            if (!acted && events.Count == 0 && refusals.Count == 0 && views.Count == 0)
            {
                // A test of a view that names nothing to expect: the read model does not exist
                var view = chapter.Steps.FirstOrDefault(x => x.Kind == EmlangElementKind.View);
                if (view is null)
                {
                    _lines.Add("// TODO: the model expects nothing here, and the slice has no view to look for");
                    context.Report.Add($"⚠ {where}: expects nothing, and its slice has no view.");
                    return;
                }

                var typeName = document(view.Label);
                _lines.Add(anyIdentity() is { } key
                    ? $"await ThenNoReadModel<{typeName}>({key});"
                    : $"await ThenNoReadModel<{typeName}>();");
            }
        }

        private void call(string open, IReadOnlyList<string> arguments, string close)
        {
            if (arguments.Count == 1)
            {
                _lines.Add(open + arguments[0] + close);
                return;
            }

            _lines.Add(open.TrimEnd().TrimEnd(','));
            if (!_lines[^1].EndsWith('(')) _lines[^1] += ",";
            for (var i = 0; i < arguments.Count; i++)
            {
                _lines.Add("    " + arguments[i] + (i < arguments.Count - 1 ? "," : close));
            }
        }

        private string partial(EmlangRef reference)
        {
            var typeName = TypeName(reference.Label);
            if (!context.Elements.Contains(typeName)) context.Elements.Add(typeName);
            var builder = new StringBuilder($"Specify<{typeName}>()");

            foreach (var (prop, text) in reference.Props)
            {
                var (member, type) = context.Member(typeName, prop, reference.Values.GetValueOrDefault(prop));
                builder.Append($".With(x => x.{member}, {value(type, text, reference.Values.GetValueOrDefault(prop))})");
            }

            foreach (var (member, variable) in _minted.GetValueOrDefault(reference) ?? [])
            {
                builder.Append($".With(x => x.{member}, {variable})");
            }

            return builder.ToString();
        }

        private string value(string type, string text, object? raw)
        {
            if (text.Length > 0 && _ids.TryGetValue(text, out var id))
            {
                if (id.Type == type) return id.Variable;
                if (type == "string") return $"{id.Variable}.ToString()";
            }

            return Literal(type, text, raw);
        }

        private string streamOf(EmlangRef reference)
        {
            var stream = streamName(reference) ?? "";
            if (stream.Length == 0)
            {
                context.Report.Add($"⚠ {where}: the event '{reference.Label}' names no stream; arranged on `object` — name its stream.");
                return "object";
            }

            context.Streams.Add(stream);
            return stream;
        }

        /// <summary>The stream's identity in an event: <c>{Stream}Id</c>, or else the first identity it names.</summary>
        private string? identityOf(string stream, EmlangRef reference)
        {
            string? fallback = null;
            foreach (var (prop, text) in reference.Props)
            {
                var (member, _) = context.Member(TypeName(reference.Label), prop, reference.Values.GetValueOrDefault(prop));
                if (!_ids.TryGetValue(text, out var id)) continue;

                if (member == stream + "Id") return id.Variable;
                fallback ??= member.EndsWith("Id", StringComparison.Ordinal) ? id.Variable : null;
            }

            return fallback;
        }

        private string keyOf(string stream, EmlangRef reference)
        {
            if (identityOf(stream, reference) is { } key) return key;
            if (_streams.TryGetValue(stream, out var minted)) return minted;

            context.Report.Add($"⚠ {where}: the {stream} event '{reference.Label}' names no identity; a fresh one is arranged.");
            return $"Guid.NewGuid() /* TODO: the model names no {stream} identity */";
        }

        /// <summary>A read model's type, recorded as a document the stubs give an id.</summary>
        private string document(string label)
        {
            var typeName = TypeName(label);
            context.Documents.Add(typeName);
            if (!context.Elements.Contains(typeName)) context.Elements.Add(typeName);
            return typeName;
        }

        /// <summary>The identity a view's example names, or else the test's first; null when there is none.</summary>
        private string? documentKey(string typeName, EmlangRef view)
        {
            foreach (var (prop, text) in view.Props)
            {
                var (member, _) = context.Member(typeName, prop, view.Values.GetValueOrDefault(prop));
                if (member.EndsWith("Id", StringComparison.Ordinal) && _ids.TryGetValue(text, out var id)) return id.Variable;
            }

            return anyIdentity();
        }

        /// <summary>The test's first identity, which is usually the stream the view is projected from.</summary>
        private string? anyIdentity() => _ids.Values.FirstOrDefault(x => x.Type == "Guid").Variable;

        /// <summary>
        /// A view arranged directly: its example as a partial, with the document's <c>Id</c> set to the
        /// identity it is keyed by, so the specs that read it back find it.
        /// </summary>
        private string documentPartial(string typeName, EmlangRef view)
        {
            var built = partial(view);
            var namesId = view.Props.Keys.Any(x => context.Member(typeName, x, view.Values.GetValueOrDefault(x)).Name == "Id");
            return !namesId && documentKey(typeName, view) is { } key
                ? built.Replace($"Specify<{typeName}>()", $"Specify<{typeName}>().With(x => x.Id, {key})")
                : built;
        }
    }

    /// <summary>An element's type name, exactly as the stub writer names its stub.</summary>
    public static string TypeName(string label)
        => CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(label));

    /// <summary>A C# expression of <paramref name="type"/> for an example value.</summary>
    public static string Literal(string type, string text, object? raw = null)
    {
        var trimmed = text.Trim();

        switch (type)
        {
            case "string":
                return quote(text);

            case CuratedFieldTypes.StringList:
                var items = raw is List<object> list
                    ? list.Select(EmlangReader.Text)
                    : trimmed.Length == 0 ? [] : [trimmed];
                var quoted = items.Select(quote).ToList();
                return quoted.Count == 0 ? "new List<string>()" : $"new List<string> {{ {string.Join(", ", quoted)} }}";
        }

        if (trimmed.Length == 0) return "default";

        return type switch
        {
            "int" when int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i.ToString(CultureInfo.InvariantCulture),
            "long" when long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l.ToString(CultureInfo.InvariantCulture) + "L",
            "decimal" when decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) => m.ToString(CultureInfo.InvariantCulture) + "m",
            "double" when double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d.ToString("R", CultureInfo.InvariantCulture) + "d",
            "bool" when bool.TryParse(trimmed, out var b) => b ? "true" : "false",
            "Guid" when Guid.TryParse(trimmed, out _) => $"Guid.Parse({quote(trimmed)})",
            "Guid" => $"Guid.NewGuid() /* {comment(trimmed)} in the model */",
            "DateTimeOffset" when DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, out _) => $"DateTimeOffset.Parse({quote(trimmed)})",
            "DateOnly" when DateOnly.TryParse(trimmed, CultureInfo.InvariantCulture, out _) => $"DateOnly.Parse({quote(trimmed)})",
            "TimeSpan" when TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out _) => $"TimeSpan.Parse({quote(trimmed)})",
            _ => $"default /* {comment(trimmed)} in the model */"
        };
    }

    private static string unique(HashSet<string> taken, string name)
    {
        var candidate = name;
        for (var i = 2; !taken.Add(candidate); i++) candidate = $"{name}_{i}";
        return candidate;
    }

    private static string snake(string text)
    {
        var words = Regex.Matches(text, "[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")
            .Select(x => x.Value.ToLowerInvariant())
            .ToList();

        var name = string.Join("_", words);
        if (name.Length == 0) return "unnamed";
        return char.IsDigit(name[0]) ? "_" + name : name;
    }

    private static string quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    private static string comment(string value) => value.Replace("*/", "* /").Replace("\n", " ");
}
