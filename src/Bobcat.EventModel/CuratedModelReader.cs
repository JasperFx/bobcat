using JasperFx.Events.EventModeling;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bobcat.EventModel;

/// <summary>
/// The result of reading a curated file: the parsed model when the YAML was at least
/// well-formed, plus every validation problem found. A file only <see cref="Succeeded"/> when it
/// parsed AND validated — mirroring <c>EventModelStore.TryStore</c>'s stance that a bad push
/// should fail loudly at the door rather than draw a blank canvas later.
/// </summary>
public sealed record CuratedModelReading(CuratedModelFile? File, IReadOnlyList<string> Problems)
{
    public bool Succeeded => File is not null && Problems.Count == 0;
}

/// <summary>
/// Parses and validates the curated event-model YAML (issue #201). Parsing is YamlDotNet with
/// camelCase members; enum-valued fields are read as strings and validated here so a typo gets a
/// named, positional problem instead of a serializer stack trace.
/// </summary>
public static class CuratedModelReader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static CuratedModelReading Read(string yaml)
    {
        CuratedModelFile? file;
        try
        {
            file = Deserializer.Deserialize<CuratedModelFile>(yaml);
        }
        catch (YamlException e)
        {
            return new CuratedModelReading(null, [$"not parseable as a curated event-model file: {e.Message}"]);
        }

        if (file is null) return new CuratedModelReading(null, ["the file was empty"]);

        return new CuratedModelReading(file, Validate(file));
    }

    public static IReadOnlyList<string> Validate(CuratedModelFile file)
    {
        var problems = new List<string>();

        if (file.Schema != 1)
        {
            problems.Add($"schema must be 1; found {file.Schema}. A missing `schema:` reads as 0 — this file may not be a curated event model at all.");
        }

        if (string.IsNullOrWhiteSpace(file.Model))
        {
            problems.Add("`model:` is required — it is the merge key the assembled Event Model folds sources by.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slice in file.Slices)
        {
            if (string.IsNullOrWhiteSpace(slice.Name))
            {
                problems.Add("every slice needs a `name:` — it is the merge key across sources.");
                continue;
            }

            if (!seen.Add(slice.Name))
            {
                problems.Add($"slice '{slice.Name}' appears more than once; slices merge by name, so declare it once.");
            }

            validateEnum<SlicePattern>(slice.Pattern, $"slice '{slice.Name}' pattern", problems);
            validateEnum<TriggerKind>(slice.Trigger?.Kind, $"slice '{slice.Name}' trigger kind", problems);

            foreach (var system in slice.ExternalSystems)
            {
                validateEnum<ExternalSystemDirection>(system.Direction, $"slice '{slice.Name}' external system '{system.Name}' direction", problems);
            }

            validateSpecifications(slice, problems);
        }

        return problems;
    }

    private static void validateSpecifications(CuratedSlice slice, List<string> problems)
    {
        if (slice.Specifications is null) return;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scenario in slice.Specifications.Scenarios)
        {
            var where = $"slice '{slice.Name}' scenario '{scenario.Name}'";

            if (string.IsNullOrWhiteSpace(scenario.Name))
            {
                problems.Add($"slice '{slice.Name}' has a scenario without a name; the name is half of the spec identity.");
                continue;
            }

            if (!names.Add(scenario.Name))
            {
                problems.Add($"{where} appears more than once; identities must be unique to join run evidence.");
            }

            var wellFormed = new List<CuratedThen>();
            foreach (var then in scenario.Then)
            {
                var set = (then.Event is not null ? 1 : 0)
                          + (then.ReadModel is not null ? 1 : 0)
                          + (then.ValidationFails is not null ? 1 : 0);
                if (set != 1)
                {
                    problems.Add($"{where}: each `then` entry needs exactly one of event / readModel / validationFails.");
                }
                else
                {
                    wellFormed.Add(then);
                }
            }

            // The grammar's shape rules, applied over the well-formed entries only so a
            // malformed one is reported once: events XOR one read model, never mixed.
            var readModels = wellFormed.Count(x => x.ReadModel is not null);
            var events = wellFormed.Count(x => x.Event is not null);
            if (readModels > 1)
            {
                problems.Add($"{where}: at most one read-model assertion per scenario.");
            }

            if (readModels > 0 && events > 0)
            {
                problems.Add($"{where}: a scenario asserts events or a read model, never both.");
            }
        }
    }

    private static void validateEnum<TEnum>(string? value, string where, List<string> problems) where TEnum : struct, Enum
    {
        if (value is null) return;
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out _)) return;

        problems.Add($"{where} '{value}' is not one of: {string.Join(" | ", Enum.GetNames<TEnum>())}.");
    }
}
