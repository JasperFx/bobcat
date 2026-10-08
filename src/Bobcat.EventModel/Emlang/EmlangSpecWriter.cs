using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JasperFx.CodeGeneration;

namespace Bobcat.EventModel.Emlang;

/// <summary>What <see cref="EmlangSpecWriter.Write"/> produced.</summary>
/// <param name="Code">The specifications file.</param>
/// <param name="Features">How many feature classes it holds: one per slice.</param>
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

        foreach (var chapter in board.Chapters)
        {
            var className = unique(classNames, FeatureClassName(chapter.Name));
            features++;

            writer.BlankLine();
            writer.WriteLine($"[BobcatFeature({quote(chapter.Name)})]");
            writer.Write($"BLOCK:public class {className}(AppFixture app) : {modelName}Spec(app)");

            if (chapter.Tests.Count == 0)
            {
                writer.WriteLine($"// The model gives \"{comment(chapter.Name)}\" no examples to generate from");
                context.Report.Add($"slice '{chapter.Name}': no tests, so no specifications.");
            }

            var methods = new HashSet<string>(StringComparer.Ordinal);
            var first = true;
            foreach (var test in chapter.Tests)
            {
                if (!first) writer.BlankLine();
                first = false;

                writer.WriteLine("[Fact]");
                writer.Write($"BLOCK:public async Task {unique(methods, MethodName(test.Name))}()");
                foreach (var line in new TestWriter(context, chapter, test).Lines())
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
            new CSharpModelWriter.StubAdditions(context.Streams.ToList(), context.Documents.ToList(), context.Elements.ToList()),
            context.Report);
    }

    /// <summary>
    /// The model's tests with no specification among <paramref name="sources"/>, as
    /// <c>slice / test</c>: what a second run reports instead of writing over the first.
    /// </summary>
    public static IReadOnlyList<string> MissingSpecs(EmlangBoard board, IEnumerable<string> sources)
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

        return board.Chapters
            .SelectMany(chapter => chapter.Tests
                .Where(test => !written.Contains((chapter.Name, MethodName(test.Name))))
                .Select(test => $"{chapter.Name} / {test.Name}"))
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
        private readonly Dictionary<string, IReadOnlyList<CSharpModelWriter.StubField>> _fields = new(StringComparer.Ordinal);

        public ImportedEventModel Model { get; } = model;
        public List<string> Report { get; } = [];
        public SortedSet<string> Streams { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Documents { get; } = new(StringComparer.Ordinal);

        /// <summary>Every command, event and view a specification names, in the order first named.</summary>
        public List<string> Elements { get; } = [];

        /// <summary>The member a prop names on a stub, and its type, exactly as the stub declares it.</summary>
        public (string Name, string Type) Member(string typeName, string prop)
        {
            var name = CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(prop));
            if (name == typeName) name += "Value";

            if (!_fields.TryGetValue(typeName, out var fields))
            {
                fields = CSharpModelWriter.FieldsOf(Model, typeName);
                _fields[typeName] = fields;
            }

            var type = fields.FirstOrDefault(x => x.Name == name)?.Type
                       ?? (name.EndsWith("Id", StringComparison.Ordinal) ? "Guid" : "string");
            return (name, type);
        }
    }

    /// <summary>One test's body. A class rather than a method because a test accumulates state as it goes: its identities.</summary>
    private sealed class TestWriter(Context context, EmlangChapter chapter, EmlangTest test)
    {
        private readonly List<string> _lines = [];

        // An identity's example value ("order-123") → the local that stands for it
        private readonly Dictionary<string, (string Variable, string Type)> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);

        private static readonly EmlangElementKind[] Elements =
            [EmlangElementKind.Command, EmlangElementKind.Event, EmlangElementKind.View];

        private string where => $"slice '{chapter.Name}', test '{test.Name}'";

        public IEnumerable<string> Lines()
        {
            declareIdentities();
            if (_ids.Count > 0) _lines.Add("");

            var arranged = given();
            act();
            assert(arranged);

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
                        var (member, type) = context.Member(typeName, prop);
                        if (type != wanted || value.Length == 0 || _ids.ContainsKey(value)) continue;
                        if (type == "string" && !member.EndsWith("Id", StringComparison.Ordinal)) continue;

                        declare(member, value, type);
                    }
                }
            }
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
                        _lines.Add($"// TODO: the model arranges the \"{comment(reference.Label)}\" view directly; arrange the events that build it");
                        context.Report.Add($"⚠ {where}: gives the view '{reference.Label}' — arrange the events that build it.");
                        break;
                }
            }

            for (var i = 0; i < groups.Count; i++)
            {
                var (stream, key, events) = groups[i];
                var verb = i == 0 ? "GivenEvents" : "GivenEventsOn";
                call($"await {verb}<{stream}>({key}, ", events, ");");
            }

            if (groups.Count == 0 && test.When.Any(x => x.Kind == EmlangElementKind.Command))
            {
                // The act runs against a stream that starts empty: name it, from the first event expected
                var expected = test.Then.FirstOrDefault(x => x.Kind == EmlangElementKind.Event && x.Actor.Length > 0);
                if (expected is not null && identityOf(streamOf(expected), expected) is { } key)
                {
                    _lines.Add($"await GivenNoEventsFor<{streamOf(expected)}>({key});");
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
                _lines.Add($"ThenRefusedWith({quote(refusal.Label)});");
                if (refusal.Props.Count > 0)
                {
                    _lines.Add($"// The model also names {string.Join(", ", refusal.Props.Keys)} on the refusal");
                    context.Report.Add($"⚠ {where}: the refusal '{refusal.Label}' carries props, which no step asserts yet.");
                }
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
                var typeName = TypeName(view.Label);
                context.Documents.Add(typeName);
                if (!context.Elements.Contains(typeName)) context.Elements.Add(typeName);
                var key = documentKey(typeName, view);

                _lines.Add(view.Props.Count == 0
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

                var typeName = TypeName(view.Label);
                context.Documents.Add(typeName);
                if (!context.Elements.Contains(typeName)) context.Elements.Add(typeName);
                _lines.Add($"await ThenNoReadModel<{typeName}>({anyIdentity(typeName)});");
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
                var (member, type) = context.Member(typeName, prop);
                builder.Append($".With(x => x.{member}, {value(type, text, reference.Values.GetValueOrDefault(prop))})");
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
            var stream = TypeName(reference.Actor);
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
                var (member, _) = context.Member(TypeName(reference.Label), prop);
                if (!_ids.TryGetValue(text, out var id)) continue;

                if (member == stream + "Id") return id.Variable;
                fallback ??= member.EndsWith("Id", StringComparison.Ordinal) ? id.Variable : null;
            }

            return fallback;
        }

        private string keyOf(string stream, EmlangRef reference)
        {
            if (identityOf(stream, reference) is { } key) return key;

            context.Report.Add($"⚠ {where}: the {stream} event '{reference.Label}' names no identity; a fresh one is arranged.");
            return $"Guid.NewGuid() /* TODO: the model names no {stream} identity */";
        }

        private string documentKey(string typeName, EmlangRef view)
        {
            foreach (var (prop, text) in view.Props)
            {
                var (member, _) = context.Member(typeName, prop);
                if (member.EndsWith("Id", StringComparison.Ordinal) && _ids.TryGetValue(text, out var id)) return id.Variable;
            }

            return anyIdentity(typeName);
        }

        /// <summary>The test's first identity, which is usually the stream the view is projected from.</summary>
        private string anyIdentity(string typeName)
        {
            var first = _ids.Values.FirstOrDefault(x => x.Type == "Guid");
            if (first.Variable is not null) return first.Variable;

            context.Report.Add($"⚠ {where}: no identity for the {typeName} document; a fresh one is used. A singleton view needs its real id.");
            return $"Guid.NewGuid() /* TODO: which {typeName}? */";
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
