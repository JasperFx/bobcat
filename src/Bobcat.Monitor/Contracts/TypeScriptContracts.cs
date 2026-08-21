using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Namotion.Reflection;
using NJsonSchema;
using NJsonSchema.CodeGeneration.TypeScript;
using NJsonSchema.Generation;

namespace Bobcat.Monitor.Contracts;

/// <summary>
/// Generates the Vue SPA's TypeScript mirrors of <see cref="MonitorEvent"/> from the C# records
/// — issue #85. CritterWatch's <c>GenerateCommand</c> pattern, cut down to what this console
/// needs: one generated file of interfaces, plus a <c>case</c> per event inserted into
/// <c>relayToStore.ts</c>.
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
/// Two rules make this re-runnable over a file someone else hand-edited, which matters because
/// contracts and their mirrors land from more than one branch:
/// <list type="bullet">
/// <item><c>monitor-events.ts</c> is owned wholesale — a hand-added interface is simply
/// regenerated from its C# record, and the result is identical if the hand-written one was
/// right.</item>
/// <item><c>relayToStore.ts</c> is patched, not owned: a case already present (hand-written or
/// generated) is left exactly as it is, and only a missing one is inserted, above the
/// <see cref="CaseMarker"/> line. The import block is merged and sorted.</item>
/// </list>
/// <c>TypeScriptContractTests</c> fails the build when either file differs from what this
/// generates, so drift is a red build rather than a silent lie in the dashboard.
/// </para>
/// </remarks>
public static class TypeScriptContracts
{
    public const string MonitorEventsFile = "monitor-events.ts";
    public const string RelayToStoreFile = "relayToStore.ts";
    public const string MessagesDirectory = "messages";

    /// <summary>The line in relayToStore.ts a generated case is inserted above.</summary>
    public const string CaseMarker = "// *CASE ABOVE* -- generated cases are inserted above this line; keep it.";

    /// <summary>How to regenerate, spelled once so every message that mentions it agrees.</summary>
    public const string GenerateCommandLine = "dotnet run --project src/Bobcat.Monitor -- generate";

    private const string frontEndSourceRelativePath = "src/Bobcat.Monitor.FrontEnd/src";

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
    /// relayToStore.ts with a case for every event type and an import for every type it routes.
    /// Returns the input unchanged when nothing is missing, which is what the drift test checks.
    /// </summary>
    public static string PatchRelayToStore(string existing)
    {
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();

        mergeImports(lines);
        insertMissingCases(lines);

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Walks up from <paramref name="start"/> (the running assembly's directory by default) to
    /// the repository root and returns <c>src/Bobcat.Monitor.FrontEnd/src</c>; null when this is
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
         * GENERATED FILE — do not edit by hand.
         *
         * TypeScript mirrors of src/Bobcat.Monitor/Contracts/MonitorEvents.cs, emitted by
         * TypeScriptContracts.cs (NJsonSchema over the C# records, through the serializer settings
         * the wire uses). Regenerate with:
         *
         *   {{GenerateCommandLine}}
         *
         * TypeScriptContractTests fails the build when this file drifts from the C# source, so a
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

    private static void mergeImports(List<string> lines)
    {
        var start = lines.FindIndex(l => l.TrimEnd() == "import type {");
        var end = start < 0 ? -1 : lines.FindIndex(start, l => l.Trim() == $"}} from './{Path.GetFileNameWithoutExtension(MonitorEventsFile)}'");
        if (start < 0 || end < 0)
        {
            throw new InvalidOperationException(
                $"{RelayToStoreFile} has no `import type {{ ... }} from './monitor-events'` block to merge the event types into.");
        }

        var names = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = start + 1; i < end; i++)
        {
            var name = lines[i].Trim().TrimEnd(',');
            if (name.Length > 0) names.Add(name);
        }

        names.Add("MonitorEnvelope");
        names.Add(nameof(BatchedWebSocketPayload));
        foreach (var (type, _) in EventTypes) names.Add(type.Name);

        lines.RemoveRange(start + 1, end - start - 1);
        lines.InsertRange(start + 1, names.Select(n => $"  {n},"));
    }

    private static void insertMissingCases(List<string> lines)
    {
        foreach (var (type, wireName) in EventTypes)
        {
            if (lines.Any(l => l.Contains($"case '{wireName}':") || l.Contains($"case \"{wireName}\":"))) continue;

            var marker = lines.FindIndex(l => l.Trim() == CaseMarker);
            if (marker < 0)
            {
                throw new InvalidOperationException(
                    $"{RelayToStoreFile} has no case for '{wireName}' and no marker line to insert one above. " +
                    $"Add this line just before `default:` in relayToStore's switch:\n    {CaseMarker}");
            }

            var indent = lines[marker][..(lines[marker].Length - lines[marker].TrimStart().Length)];
            lines.InsertRange(marker,
            [
                $"{indent}case '{wireName}':",
                $"{indent}  runs.handle{type.Name}(envelope.data as {type.Name})",
                $"{indent}  break"
            ]);
        }
    }
}
