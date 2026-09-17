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
public sealed record CuratedModelReading(
    CuratedModelFile? File,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings = null!)
{
    /// <summary>
    /// Findings that do not invalidate the file. Separate from <see cref="Problems"/> on purpose:
    /// a problem means the model cannot be used, and existing models would break if a warning
    /// counted as one. A caller that prints only problems is silently dropping these (issue #318).
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Warnings ?? [];

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

        return new CuratedModelReading(file, Validate(file), Warn(file));
    }

    /// <summary>
    /// Findings worth reporting that do not invalidate the file (issue #318).
    /// </summary>
    /// <remarks>
    /// An unrecognised <c>fields:</c> type is the motivating case. A declaration names a type, and
    /// an unknown one is silently emitted as <c>string</c> — so <c>statuses: Dictionary&lt;Guid,
    /// string&gt;</c> became <c>public string Statuses</c>, the model saying one thing and the code
    /// another with nothing in between saying so. A warning rather than a problem because models
    /// relying on the fallback exist and must keep loading.
    ///
    /// Scenario values are deliberately not checked: there the sketch IS a sample and
    /// <c>string</c> is the right answer.
    /// </remarks>
    public static IReadOnlyList<string> Warn(CuratedModelFile file)
    {
        var warnings = new List<string>();

        foreach (var slice in file.Slices)
        {
            foreach (var (typeName, element) in slice.Elements)
            {
                foreach (var (fieldName, sketch) in element.Fields)
                {
                    if (CuratedFieldTypes.TryInfer(sketch, out _)) continue;

                    warnings.Add(
                        $"slice '{slice.Name}', element '{typeName}', field '{fieldName}': "
                        + $"'{sketch}' is not a type this format knows, so it will be emitted as `string`. "
                        + $"Known types: {string.Join(" | ", CuratedFieldTypes.Known)}. "
                        + "There is no collection field — if a projection needs per-item state, put what "
                        + "it needs on the event instead.");
                }
            }
        }

        // An arranged event pointed at an aggregate no slice declares (issue #320). A warning and
        // not a problem: the step it scaffolds is still valid Gherkin and the type may exist in
        // code the model has not caught up with — but far more often it is a typo, and the
        // scaffolded arrange then silently addresses a stream of a type nothing else mentions.
        var declared = file.Slices.SelectMany(x => x.Aggregates).ToHashSet(StringComparer.Ordinal);
        foreach (var slice in file.Slices)
        {
            foreach (var scenario in slice.Specifications?.Scenarios ?? [])
            {
                foreach (var given in scenario.Given)
                {
                    if (given.Aggregate is null || declared.Contains(given.Aggregate)) continue;

                    warnings.Add(
                        $"slice '{slice.Name}', scenario '{scenario.Name}': arranged event "
                        + $"'{given.Event}' names aggregate '{given.Aggregate}', which no slice in "
                        + "this model declares in `aggregates:`.");
                }
            }
        }

        return warnings;
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
                          + (then.ValidationFails is not null ? 1 : 0)
                          + (then.RefusedWith is not null ? 1 : 0);
                if (set != 1)
                {
                    problems.Add($"{where}: each `then` entry needs exactly one of event / readModel / validationFails / refusedWith.");
                }
                else
                {
                    wellFormed.Add(then);
                }

                if (then.Id is not null && then.ReadModel is null)
                {
                    problems.Add($"{where}: `id:` names the read-model document to assert on, so it only belongs on a `readModel:` entry.");
                }

                validateRefusal(slice, then.RefusedWith, where, problems);
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

    /// <summary>
    /// The rules on a stated HTTP refusal (issue #337). All three are refusals of the FILE rather
    /// than of the design: a status off the HTTP lane has nowhere to be asserted, a status outside
    /// 4xx/5xx is not a refusal, and a reason-less one scaffolds a guard TODO nobody can act on.
    /// </summary>
    private static void validateRefusal(
        CuratedSlice slice, CuratedRefusal? refusal, string where, List<string> problems)
    {
        if (refusal is null) return;

        if (!CuratedSliceShape.AnswersOverHttp(slice))
        {
            problems.Add(
                $"{where}: `refusedWith:` states an HTTP status, and this slice does not answer over HTTP "
                + "— it refuses by throwing, which `Then validation fails with \"…\"` asserts. "
                + "Use `validationFails:` here, or declare the slice as a Command slice with an "
                + "Http or Human trigger.");
        }

        if (refusal.Status is < 400 or > 599)
        {
            problems.Add(
                $"{where}: `refusedWith.status:` is {refusal.Status}; a refusal answers with a 4xx or a 5xx. "
                + "A missing `status:` reads as 0.");
        }

        if (string.IsNullOrWhiteSpace(refusal.Reason))
        {
            problems.Add(
                $"{where}: `refusedWith.reason:` is required — it is the sentence the scaffolded guard's "
                + "`Detail` and the feature's comment beside the status both carry. A status alone says "
                + "what the endpoint answers and nothing about why.");
        }
    }

    private static void validateEnum<TEnum>(string? value, string where, List<string> problems) where TEnum : struct, Enum
    {
        if (value is null) return;
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out _)) return;

        problems.Add($"{where} '{value}' is not one of: {string.Join(" | ", Enum.GetNames<TEnum>())}.");
    }
}
