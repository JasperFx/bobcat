using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Namotion.Reflection;
using NJsonSchema;
using NJsonSchema.CodeGeneration.TypeScript;
using NJsonSchema.Generation;

namespace Bobcat.Ui.Contracts;

/// <summary>
/// Generates the Vue SPA's TypeScript mirrors of <see cref="MonitorEvent"/> from the C# records
/// — issue #85. CritterWatch's <c>GenerateCommand</c> pattern, cut down to what this console
/// needs: one file of interfaces (<c>monitor-events.ts</c>) and one of routes
/// (<c>relayRoutes.ts</c>, the event → runs-store case table).
/// </summary>
/// <remarks>
/// <para>
/// The records are reflected through NJsonSchema using the SAME System.Text.Json settings the
/// wire uses (web defaults — camelCase — and string enums), so the mirror can only ever say what
/// the serializer does: a property the serializer renames, skips or nulls is mirrored that way
/// without anyone remembering to. The wire names come from the <c>[JsonDerivedType]</c>
/// discriminators on <see cref="MonitorEvent"/>, which <c>SignalRBatchingTests</c> pins equal to
/// Wolverine's snake_case message names — so the generator has exactly one spelling to emit.
/// </para>
/// <para>
/// <b>Both files are built, not committed (bobcat#604).</b> They are gitignored and written by the
/// Nuke <c>UiContracts</c> target (which <c>UiSmoke</c>, <c>Pack</c> and <c>ui-frontend.yml</c>
/// run) and by the frontend's npm <c>prepare</c>/<c>predev</c>/<c>pretest</c>/<c>prebuild</c> hooks.
/// Parallel branches that touched the wire used to conflict on them even when their C# merged
/// cleanly. Each file is owned wholesale by this generator; nothing hand-written is patched.
/// The hand-written <c>relayToStore.ts</c> imports <c>routeMonitorEvent</c> from the routes file
/// and keeps the cases that are not a <see cref="MonitorEvent"/> (the batched frame, the Event
/// Model change notice).
/// </para>
/// </remarks>
public static class TypeScriptContracts
{
    public const string MonitorEventsFile = "monitor-events.ts";
    public const string RelayRoutesFile = "relayRoutes.ts";

    /// <summary>The results file's types (bobcat#541), generated from its JSON Schema.</summary>
    public const string ResultsFileTypesFile = "results-file.ts";
    public const string MessagesDirectory = "messages";

    /// <summary>The generated files, in the order they are written.</summary>
    public static IReadOnlyList<string> GeneratedFiles { get; } = [MonitorEventsFile, RelayRoutesFile, ResultsFileTypesFile];

    /// <summary>How to regenerate, spelled once so every message that mentions it agrees.</summary>
    public const string GenerateCommandLine = "./build.sh UiContracts";

    private const string frontEndSourceRelativePath = "src/Bobcat.Ui.FrontEnd/src";

    private static readonly JsonSerializerOptions wireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Every concrete event the wire carries, with its snake_case wire name — read from the
    /// <c>[JsonDerivedType]</c> attributes on <see cref="MonitorEvent"/>, in declaration order.
    /// </summary>
    public static IReadOnlyList<(Type Type, string WireName)> EventTypes
        => typeof(MonitorEvent)
            .GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .Select(a => (a.DerivedType, (string)a.TypeDiscriminator!))
            .ToList();

    /// <summary>The full contents of monitor-events.ts.</summary>
    public static string GenerateMonitorEvents()
    {
        var builder = new StringBuilder();
        builder.Append(header());
        builder.Append(envelopePreamble());
        builder.Append(wireNameUnion());
        builder.Append(interfaces());
        return builder.ToString();
    }

    /// <summary>
    /// The full contents of relayRoutes.ts: <c>routeMonitorEvent(runs, envelope)</c>, one case per
    /// event calling the runs store's <c>handle{Type}</c>, returning false for a type it does not
    /// route. A store missing a handler for a new event is a vue-tsc error, which is the point.
    /// </summary>
    public static string GenerateRelayRoutes()
    {
        var builder = new StringBuilder();
        builder.Append($$"""
            /**
             * GENERATED FILE — do not edit by hand, and do not commit it (it is gitignored).
             *
             * The event → runs-store routes for every MonitorEvent in src/Bobcat.Ui/Contracts/MonitorEvents.cs,
             * emitted by TypeScriptContracts.cs. relayToStore.ts (hand-written) calls routeMonitorEvent
             * for anything it does not handle itself. Regenerate with:
             *
             *   {{GenerateCommandLine}}
             *
             * or any of the frontend's npm scripts, whose pre-hooks run it after one `dotnet build`.
             */
            import type { useRunsStore } from '@/stores/runs-store'
            import type {

            """);

        var names = new SortedSet<string>(EventTypes.Select(e => e.Type.Name), StringComparer.Ordinal) { "MonitorEnvelope" };
        foreach (var name in names) builder.Append($"  {name},\n");

        builder.Append($$"""
            } from './{{Path.GetFileNameWithoutExtension(MonitorEventsFile)}}'

            /** The runs store, as the routes see it. */
            type RunsStore = ReturnType<typeof useRunsStore>

            /**
             * Route one envelope to the runs store's handler for its event. Returns false for a type this
             * table does not route, which relayToStore handles itself or ignores (forward compatibility).
             */
            export function routeMonitorEvent(runs: RunsStore, envelope: MonitorEnvelope): boolean {
              switch (envelope.type) {

            """);

        foreach (var (type, wireName) in EventTypes)
        {
            builder.Append($"    case '{wireName}':\n");
            builder.Append($"      runs.handle{type.Name}(envelope.data as {type.Name})\n");
            builder.Append("      return true\n");
        }

        builder.Append("""
                default:
                  return false
              }
            }

            """);

        return builder.ToString();
    }

    /// <summary>
    /// The full contents of results-file.ts: the results file's shape (bobcat#540) as TypeScript,
    /// for the HTML results page (bobcat#541).
    /// </summary>
    /// <remarks>
    /// Generated from the results file's JSON SCHEMA text rather than from records, because this
    /// assembly references nothing in core (the receiver keeps its own copy of what it reads), and
    /// the schema is the contract the file publishes anyway. The tool, which references both, hands
    /// the text over (<c>ResultsFile.SchemaText</c>). Every member is optional: the page may be
    /// asked to render a file an older Bobcat wrote.
    /// </remarks>
    public static string GenerateResultsFile(string resultsSchema)
    {
        var schema = JsonSchema.FromJsonAsync(resultsSchema).GetAwaiter().GetResult();

        var settings = new TypeScriptGeneratorSettings
        {
            TypeStyle = TypeScriptTypeStyle.Interface,
            TypeScriptVersion = 5.0m,
            ExportTypes = true,
            DateTimeType = TypeScriptDateTimeType.String,
            NullValue = TypeScriptNullValue.Null,
            EnumStyle = TypeScriptEnumStyle.StringLiteral,
            MarkOptionalProperties = true,
            GenerateConstructorInterface = false,
            TypeNameGenerator = new ResultsTypeNames()
        };

        var code = new TypeScriptGenerator(schema, settings).GenerateFile(ResultsTypeNames.Root);

        var builder = new StringBuilder();
        builder.Append($$"""
            /**
             * GENERATED FILE — do not edit by hand, and do not commit it (it is gitignored).
             *
             * The results file's shape (BOBCAT_RESULTS, `bobcat run --results`), emitted by
             * TypeScriptContracts.cs from the file's own JSON Schema (ResultsFile.GenerateSchema), for
             * the HTML results page. Regenerate with:
             *
             *   {{GenerateCommandLine}}
             *
             * or any of the frontend's npm scripts, whose pre-hooks run it after one `dotnet build`.
             */


            """);

        // The index signature NJsonSchema adds for an open schema says nothing the page may rely on,
        // and would let a misspelled member compile.
        var lines = toHouseStyle(code).Split('\n')
            .Where(line => line.Trim() != "[key: string]: any");
        var text = string.Join('\n', lines);
        while (text.Contains("\n\n}")) text = text.Replace("\n\n}", "\n}");
        builder.Append(withoutRepeatedDeclarations(text));
        return builder.ToString();
    }

    /// <summary>
    /// The schema's object types are anonymous, so NJsonSchema would name them after the property
    /// holding them (<c>Scenarios</c>, <c>ScenarioFailureCells</c>, and <c>Scenarios2</c> for the
    /// timing block's). These are named by what they carry instead, which is also what keeps the
    /// names stable when a property is added somewhere else in the file.
    /// </summary>
    private sealed class ResultsTypeNames : DefaultTypeNameGenerator
    {
        public const string Root = "BobcatResults";

        // A member only that type has, in the order they are tried.
        private static readonly (string Member, string Name)[] shapes =
        [
            ("schemaVersion", Root),
            ("runId", "ResultsRun"),
            ("acceptanceFailing", "ResultsSummary"),
            ("verdict", "ResultsScenario"),
            ("measuredMs", "ResultsTiming"),
            ("wallClockMs", "ResultsScenarioTiming"),
            ("occurrences", "ResultsStepCost"),
            ("before", "ResultsGap"),
            ("stepId", "ResultsStep"),
            ("mediaType", "ResultsAttachment"),
            ("shortTitle", "ResultsReport"),
            ("description", "ResultsGridRow"),
            ("columns", "ResultsGrid"),
            ("expected", "ResultsCell"),
            ("rights", "ResultsCounts")
        ];

        public override string Generate(JsonSchema schema, string? typeNameHint, IEnumerable<string> reservedTypeNames)
        {
            var actual = schema.ActualSchema;
            foreach (var (member, name) in shapes)
            {
                // Reused even when taken: the same shape reached twice (a report's grid and a step's
                // set verification) is one type, and the duplicate declaration is dropped below.
                if (actual.Properties.ContainsKey(member)) return name;
            }

            return base.Generate(schema, typeNameHint, reservedTypeNames);
        }
    }

    /// <summary>Drop an interface declared a second time with the same body.</summary>
    private static string withoutRepeatedDeclarations(string code)
    {
        var blocks = code.Split("\n\n");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return string.Join("\n\n", blocks.Where(block => !block.StartsWith("export interface ", StringComparison.Ordinal) || seen.Add(block)));
    }

    /// <summary>
    /// Walks up from <paramref name="start"/> (the running assembly's directory by default) to
    /// the repository root and returns <c>src/Bobcat.Ui.FrontEnd/src</c>; null when this is
    /// not running inside the repository (a packed tool, say).
    /// </summary>
    public static string? FindFrontEndSourceDirectory(string? start = null)
    {
        var directory = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, frontEndSourceRelativePath);
            if (Directory.Exists(Path.Combine(candidate, MessagesDirectory))) return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    private static string header() =>
        $$"""
        /**
         * GENERATED FILE — do not edit by hand, and do not commit it (it is gitignored).
         *
         * TypeScript mirrors of src/Bobcat.Ui/Contracts/MonitorEvents.cs, emitted by
         * TypeScriptContracts.cs (NJsonSchema over the C# records, through the serializer settings
         * the wire uses). Regenerate with:
         *
         *   {{GenerateCommandLine}}
         *
         * or any of the frontend's npm scripts, whose pre-hooks run it after one `dotnet build`. A
         * new or changed record shows up here by regenerating, never by hand-editing. The envelope
         * `type` strings are Wolverine's snake_case message type names, which the C# side pins as
         * the JSON type discriminators too — one spelling everywhere.
         */


        """;

    /// <summary>
    /// The one hand-maintained shape in the file: the {type, data} envelope relayToStore switches
    /// on. It is the SignalR transport's frame, not a contract record, so there is no C# to
    /// generate it from.
    /// </summary>
    private static string envelopePreamble() =>
        """
        /** The {type, data} frame relayToStore switches on — the transport's shape, not a contract record. */
        export interface MonitorEnvelope {
          type: string
          data: unknown
        }


        """;

    private static string wireNameUnion()
    {
        var builder = new StringBuilder();
        builder.AppendLine("/** Every envelope `type` the monitor relays — the [JsonDerivedType] discriminators on MonitorEvent. */");
        builder.AppendLine("export type MonitorEventType =");
        foreach (var (_, wireName) in EventTypes)
        {
            builder.AppendLine($"  | '{wireName}'");
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private static string interfaces()
    {
        var schemaSettings = new SystemTextJsonSchemaGeneratorSettings
        {
            SerializerOptions = wireOptions,
            // Keep the hierarchy: each event `extends MonitorEvent`, and the base carries the
            // `type` discriminator, which is what an archived NDJSON line looks like.
            FlattenInheritanceHierarchy = false
        };

        var generatorSettings = new TypeScriptGeneratorSettings
        {
            TypeStyle = TypeScriptTypeStyle.Interface,
            TypeScriptVersion = 5.0m,
            ExportTypes = true,
            // Wire shapes, not view models: ISO strings stay strings, C# null is TS null, and an
            // enum (should one ever appear) is a string-literal union, never a runtime enum.
            DateTimeType = TypeScriptDateTimeType.String,
            NullValue = TypeScriptNullValue.Null,
            EnumStyle = TypeScriptEnumStyle.StringLiteral,
            MarkOptionalProperties = false,
            GenerateConstructorInterface = false
        };

        // One schema document rooted at MonitorEvent, which reaches every derived event through
        // the polymorphism attributes, plus the batching envelope registered into the same
        // resolver so its `data: MonitorEvent` reference binds to the same interface.
        var root = new JsonSchema();
        var resolver = new JsonSchemaResolver(root, schemaSettings);
        var generator = new JsonSchemaGenerator(schemaSettings);
        generator.Generate(root, typeof(MonitorEvent).ToContextualType(), resolver);
        generator.Generate(typeof(BatchedWebSocketPayload), resolver);

        var code = new TypeScriptGenerator(root, generatorSettings).GenerateFile();
        return toHouseStyle(code);
    }

    /// <summary>
    /// NJsonSchema's emission, reshaped to the repository's TypeScript style: no banner, two-space
    /// indent, no semicolons, a wire-name doc comment on every event, and additive members
    /// (constructor parameters with a default) marked optional.
    /// </summary>
    private static string toHouseStyle(string code)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n').ToList();

        // NJsonSchema's "auto-generated" banner and the blank lines after it.
        while (lines.Count > 0 && !lines[0].StartsWith("export ", StringComparison.Ordinal))
        {
            lines.RemoveAt(0);
        }

        var wireNames = EventTypes.ToDictionary(e => e.Type.Name, e => e.WireName);
        var optional = optionalMembers();
        string? currentType = null;

        var output = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("    ", StringComparison.Ordinal)) line = "  " + line[4..];
            if (line.EndsWith(';')) line = line[..^1];

            if (line.StartsWith("export interface ", StringComparison.Ordinal))
            {
                currentType = line["export interface ".Length..].Split(' ', '{')[0];
                if (wireNames.TryGetValue(currentType, out var wireName))
                {
                    output.Add($"/** Envelope type: '{wireName}' */");
                }
            }
            else if (currentType == nameof(MonitorEvent) && line == "  type: string")
            {
                // The polymorphic discriminator rides on every archived line and relayed item,
                // but relayToStore has already switched on it by the time a store handler sees
                // the event — so it is typed precisely and left optional, and a handler's test
                // can build an event without spelling it.
                output.Add("  /** The STJ discriminator — already dispatched on by relayToStore, so handlers never need it. */");
                line = "  type?: MonitorEventType";
            }
            else if (currentType is not null
                     && optional.TryGetValue(currentType, out var members)
                     && line.StartsWith("  ", StringComparison.Ordinal))
            {
                var colon = line.IndexOf(':');
                if (colon > 2 && members.Contains(line[2..colon]))
                {
                    line = line[..colon] + "?" + line[colon..];
                }
            }

            output.Add(line);
        }

        // Exactly one blank line between declarations, one newline at the end of the file.
        var text = string.Join('\n', output).Trim('\n');
        while (text.Contains("\n\n\n")) text = text.Replace("\n\n\n", "\n\n");
        return text + "\n";
    }

    /// <summary>
    /// A record constructor parameter with a default value is one an older publisher may omit —
    /// that is what "additive" means on this wire — so its mirror is an optional member. Keyed by
    /// the TypeScript interface name, valued by the camelCase member names.
    /// </summary>
    private static Dictionary<string, HashSet<string>> optionalMembers()
    {
        var result = new Dictionary<string, HashSet<string>>();
        foreach (var (type, _) in EventTypes)
        {
            var members = type.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Where(p => p.HasDefaultValue && p.Name is not null)
                .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name!))
                .ToHashSet(StringComparer.Ordinal);

            if (members.Count > 0) result[type.Name] = members;
        }

        return result;
    }
}
