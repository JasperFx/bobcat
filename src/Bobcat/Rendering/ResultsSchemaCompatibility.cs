using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bobcat.Rendering;

/// <summary>
/// Compares the results file's schema (<see cref="ResultsFile.GenerateSchema"/>) with a baseline,
/// the copy packed in the last released Bobcat package, and names every breaking change
/// (bobcat#604). This is the review signal the committed schema used to give, aimed at what
/// consumers actually have rather than at whatever the last merge committed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Breaking:</b> a property the baseline describes that is gone, a property whose JSON type
/// set changed (narrowed, widened or replaced: <c>string</c> → <c>["string","null"]</c> counts,
/// because a reader that never saw null now can), a format or enum value that changed or went, and
/// a property that is newly required. <b>Additive</b> (passes): a new optional property, a new
/// enum value, a changed description.
/// </para>
/// <para>
/// A breaking change is allowed when <see cref="ResultsFile.SchemaVersion"/> moved, which renames
/// the schema file; that is the only way a consumer can tell the shapes apart.
/// </para>
/// </remarks>
public static class ResultsSchemaCompatibility
{
    private static readonly Regex versionInFileName = new(@"^bobcat-results-(?<version>.+)\.schema\.json$", RegexOptions.CultureInvariant);

    /// <summary>The schema version a schema file's name carries, or null when it is not one.</summary>
    public static string? VersionOf(string schemaFileName)
    {
        var match = versionInFileName.Match(Path.GetFileName(schemaFileName));
        return match.Success ? match.Groups["version"].Value : null;
    }

    /// <summary>Every breaking change from <paramref name="baseline"/> to <paramref name="current"/>, as one line each.</summary>
    public static IReadOnlyList<string> BreakingChanges(string baseline, string current)
    {
        var baselineRoot = JsonNode.Parse(baseline) as JsonObject ?? throw new ArgumentException("The baseline schema is not a JSON object", nameof(baseline));
        var currentRoot = JsonNode.Parse(current) as JsonObject ?? throw new ArgumentException("The current schema is not a JSON object", nameof(current));

        var changes = new List<string>();
        compare(baselineRoot, baselineRoot, currentRoot, currentRoot, "", changes, new HashSet<(JsonObject, JsonObject)>(ReferencePairComparer.Instance));
        return changes;
    }

    /// <summary>
    /// The verdict for one comparison, as the CI check prints it. Breaking changes fail it only
    /// when the version did not move.
    /// </summary>
    public static (bool Passed, IReadOnlyList<string> Lines) Check(
        string baseline, string baselineVersion, string current, string currentVersion, string baselineDescription)
    {
        var breaking = BreakingChanges(baseline, current);
        var lines = new List<string>();

        if (breaking.Count == 0)
        {
            lines.Add($"The results schema {currentVersion} is compatible with {baselineDescription} ({baselineVersion}).");
            return (true, lines);
        }

        if (baselineVersion != currentVersion)
        {
            lines.Add($"The results schema has {breaking.Count} breaking change(s) from {baselineDescription}, and SchemaVersion moved {baselineVersion} → {currentVersion}, so they are allowed:");
            lines.AddRange(breaking.Select(b => "  " + b));
            return (true, lines);
        }

        lines.Add($"The results schema has {breaking.Count} breaking change(s) from {baselineDescription} without a SchemaVersion change ({currentVersion}):");
        lines.AddRange(breaking.Select(b => "  " + b));
        lines.Add("Make the change additive (a new optional property instead), or move ResultsFile.SchemaVersion if consumers really must change.");
        return (false, lines);
    }

    private static void compare(JsonObject baselineRoot, JsonObject baseline, JsonObject currentRoot, JsonObject current,
        string path, List<string> changes, HashSet<(JsonObject, JsonObject)> visited)
    {
        baseline = resolve(baselineRoot, baseline);
        current = resolve(currentRoot, current);
        if (!visited.Add((baseline, current))) return;

        var at = path.Length == 0 ? "the root" : path;

        var baselineTypes = typesOf(baseline);
        var currentTypes = typesOf(current);
        if (baselineTypes is not null && !baselineTypes.SetEquals(currentTypes ?? []))
        {
            changes.Add($"{at}: type changed from {describe(baselineTypes)} to {(currentTypes is null ? "unconstrained" : describe(currentTypes))}");
        }

        var baselineFormat = baseline["format"]?.GetValue<string>();
        var currentFormat = current["format"]?.GetValue<string>();
        if (baselineFormat is not null && baselineFormat != currentFormat)
        {
            changes.Add($"{at}: format changed from '{baselineFormat}' to {(currentFormat is null ? "none" : $"'{currentFormat}'")}");
        }

        if (baseline["enum"] is JsonArray baselineEnum)
        {
            var currentValues = (current["enum"] as JsonArray)?.Select(v => v?.ToJsonString()).ToHashSet();
            foreach (var value in baselineEnum.Select(v => v?.ToJsonString()))
            {
                if (currentValues is not null && !currentValues.Contains(value))
                {
                    changes.Add($"{at}: enum value {value} removed");
                }
            }
        }

        var baselineRequired = requiredOf(baseline);
        foreach (var name in requiredOf(current).Where(n => !baselineRequired.Contains(n)).Order(StringComparer.Ordinal))
        {
            changes.Add($"{join(path, name)}: newly required");
        }

        if (baseline["properties"] is JsonObject baselineProperties)
        {
            var currentProperties = current["properties"] as JsonObject;
            foreach (var (name, node) in baselineProperties)
            {
                if (node is not JsonObject baselineProperty) continue;
                if (currentProperties?[name] is not JsonObject currentProperty)
                {
                    changes.Add($"{join(path, name)}: property removed");
                    continue;
                }

                compare(baselineRoot, baselineProperty, currentRoot, currentProperty, join(path, name), changes, visited);
            }
        }

        if (baseline["items"] is JsonObject baselineItems)
        {
            if (current["items"] is JsonObject currentItems)
            {
                compare(baselineRoot, baselineItems, currentRoot, currentItems, path + "[]", changes, visited);
            }
            else
            {
                changes.Add($"{at}: item schema removed");
            }
        }

        if (baseline["additionalProperties"] is JsonObject baselineValues && current["additionalProperties"] is JsonObject currentAdditional)
        {
            compare(baselineRoot, baselineValues, currentRoot, currentAdditional, path + "{}", changes, visited);
        }
    }

    private static JsonObject resolve(JsonObject root, JsonObject node)
    {
        for (var hops = 0; hops < 32 && node["$ref"]?.GetValue<string>() is { } reference; hops++)
        {
            if (!reference.StartsWith("#", StringComparison.Ordinal)) return node;

            JsonNode? target = root;
            foreach (var segment in reference.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var key = segment.Replace("~1", "/").Replace("~0", "~");
                target = target switch
                {
                    JsonObject obj => obj[key],
                    JsonArray array when int.TryParse(key, out var index) && index < array.Count => array[index],
                    _ => null
                };
            }

            if (target is not JsonObject resolved) return node;
            node = resolved;
        }

        return node;
    }

    private static HashSet<string>? typesOf(JsonObject node) => node["type"] switch
    {
        JsonValue value => [value.GetValue<string>()],
        JsonArray array => array.Select(t => t!.GetValue<string>()).ToHashSet(StringComparer.Ordinal),
        _ => null
    };

    private static HashSet<string> requiredOf(JsonObject node)
        => (node["required"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];

    private static string describe(HashSet<string> types)
        => types.Count == 1 ? types.Single() : "[" + string.Join(", ", types.Order(StringComparer.Ordinal)) + "]";

    private static string join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private sealed class ReferencePairComparer : IEqualityComparer<(JsonObject, JsonObject)>
    {
        public static readonly ReferencePairComparer Instance = new();

        public bool Equals((JsonObject, JsonObject) x, (JsonObject, JsonObject) y)
            => ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);

        public int GetHashCode((JsonObject, JsonObject) obj)
            => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Item1),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Item2));
    }
}
