using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bobcat.EventModel;

/// <summary>
/// The result of reading a spec-ownership manifest — the parsed file when the YAML was at least
/// well-formed, plus every problem and warning found. Mirrors <see cref="CuratedModelReading"/>
/// deliberately: the two files are read by the same callers, and a second result shape would only
/// invite one of them to drop its <see cref="Warnings"/> (issue #318).
/// </summary>
public sealed record SpecOwnershipReading(
    SpecOwnershipFile? File,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings = null!)
{
    /// <inheritdoc cref="CuratedModelReading.Warnings"/>
    public IReadOnlyList<string> Warnings { get; init; } = Warnings ?? [];

    public bool Succeeded => File is not null && Problems.Count == 0;

    public static SpecOwnershipReading Empty { get; } = new(new SpecOwnershipFile { Schema = 1 }, []);
}

/// <summary>
/// Parses and validates the spec-ownership manifest (issue #324 part 4).
/// </summary>
/// <remarks>
/// Validation is split in two because the two halves are available at different times.
/// <see cref="Read"/> checks what the file can say about itself — schema, duplicates, the one
/// impossible <c>kind</c>/<c>authoring</c> corner. <see cref="Validate(SpecOwnershipFile,
/// CuratedModelFile)"/> checks the join to the event model, which needs both files in hand.
/// </remarks>
public static class SpecOwnershipReader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static SpecOwnershipReading Read(string yaml)
    {
        SpecOwnershipFile? file;
        try
        {
            file = Deserializer.Deserialize<SpecOwnershipFile>(yaml);
        }
        catch (YamlException e)
        {
            return new SpecOwnershipReading(null, [$"not parseable as a spec-ownership manifest: {e.Message}"]);
        }

        if (file is null) return new SpecOwnershipReading(null, ["the file was empty"]);

        return new SpecOwnershipReading(file, Validate(file));
    }

    /// <summary>Read the manifest and check it against the model it claims to describe, in one call.</summary>
    public static SpecOwnershipReading Read(string yaml, CuratedModelFile model)
    {
        var reading = Read(yaml);
        if (reading.File is null) return reading;

        return reading with
        {
            Problems = [.. reading.Problems, .. Validate(reading.File, model)],
            Warnings = [.. reading.Warnings, .. Warn(reading.File, model)]
        };
    }

    /// <summary>What the manifest can be judged on without the event model in hand.</summary>
    public static IReadOnlyList<string> Validate(SpecOwnershipFile file)
    {
        var problems = new List<string>();

        if (file.Schema != 1)
        {
            problems.Add($"schema must be 1; found {file.Schema}. A missing `schema:` reads as 0 — this file may not be a spec-ownership manifest at all.");
        }

        if (string.IsNullOrWhiteSpace(file.Model))
        {
            problems.Add("`model:` is required — it is how this manifest joins the event model it describes.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in file.Slices)
        {
            if (string.IsNullOrWhiteSpace(entry.Slice))
            {
                problems.Add("every entry needs a `slice:` — it is the merge key this manifest joins on.");
                continue;
            }

            if (!seen.Add(entry.Slice))
            {
                problems.Add($"slice '{entry.Slice}' is listed more than once; one slice is specified in one place, which is the whole point of this file.");
            }

            var kindKnown = entry.Kind is null || SpecOwnershipVocabulary.TryParseKind(entry.Kind, out _);
            if (!kindKnown)
            {
                problems.Add($"slice '{entry.Slice}': kind '{entry.Kind}' is not one of: {string.Join(" | ", SpecOwnershipVocabulary.KindNames)}.");
            }

            var authoringKnown = entry.Authoring is null || SpecOwnershipVocabulary.TryParseAuthoring(entry.Authoring, out _);
            if (!authoringKnown)
            {
                problems.Add($"slice '{entry.Slice}': authoring '{entry.Authoring}' is not one of: {string.Join(" | ", SpecOwnershipVocabulary.AuthoringNames)}.");
            }

            if (!kindKnown || !authoringKnown) continue;

            // The one impossible corner of two otherwise orthogonal axes. A rule and not a
            // comment: both Gherkin and code-first run through the fixture and therefore the
            // store, so `unit` with either is a contradiction, and silently honouring the
            // authoring would hand back a `.feature` to an author who asked for a unit test.
            if (entry.ResolvedKind == SpecKind.Unit && entry.ResolvedAuthoring != SpecAuthoring.Projected)
            {
                problems.Add(
                    $"slice '{entry.Slice}': kind 'unit' cannot be authored as '{entry.Authoring}' — both gherkin and "
                    + "code-first run through the fixture and so through the store. A unit-tested slice is `authoring: projected`.");
            }

            if (entry.ResolvedKind == SpecKind.Unit && string.IsNullOrWhiteSpace(entry.CoveredBy))
            {
                problems.Add(
                    $"slice '{entry.Slice}': `coveredBy:` is required for a unit-tested slice — name the "
                    + "{Feature}/{Scenario} that runs this slice's command end to end.");
            }
        }

        foreach (var group in file.Slices
                     .Where(x => !string.IsNullOrWhiteSpace(x.Owner))
                     .GroupBy(x => x.Owner!, StringComparer.Ordinal))
        {
            var styles = group.Select(x => x.ResolvedAuthoring).Distinct().ToList();
            if (styles.Count > 1)
            {
                problems.Add(
                    $"owner '{group.Key}' is named by slices authored {string.Join(" and ", styles.Select(x => $"'{x.ToString().ToLowerInvariant()}'"))}. "
                    + "One type is one authoring style — a class is either a Specification or a projected test, not both.");
            }
        }

        return problems;
    }

    /// <summary>
    /// The join to the event model, checked in the direction both files can see: every slice named
    /// here must exist there, and every <c>coveredBy</c> must name a scenario it actually declares.
    /// </summary>
    /// <remarks>
    /// The other direction — a type carrying <c>[BobcatSlice]</c> for a slice this manifest does
    /// not list — needs the compilation, so it lives in the generator as BOBCAT025/BOBCAT026.
    /// Together they are the duplicate-identity guard; either alone leaves the gap that separating
    /// the manifest from the model would otherwise reintroduce.
    /// </remarks>
    public static IReadOnlyList<string> Validate(SpecOwnershipFile file, CuratedModelFile model)
    {
        var problems = new List<string>();

        if (!string.IsNullOrWhiteSpace(file.Model)
            && !string.Equals(file.Model, model.Model, StringComparison.Ordinal))
        {
            problems.Add(
                $"this manifest says `model: {file.Model}` but the event model is '{model.Model}'. They join on that "
                + "name, so a mismatch means the manifest is describing some other model's slices.");
        }

        var slices = model.Slices.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var identities = DeclaredIdentities(model);

        foreach (var entry in file.Slices)
        {
            if (string.IsNullOrWhiteSpace(entry.Slice)) continue;

            if (!slices.Contains(entry.Slice))
            {
                problems.Add(
                    $"slice '{entry.Slice}' is not in the event model. This file is keyed on slice names and cannot be "
                    + "derived, so a renamed or deleted slice leaves an entry pointing at nothing.");
            }

            if (string.IsNullOrWhiteSpace(entry.CoveredBy)) continue;

            if (!identities.Contains(entry.CoveredBy!))
            {
                problems.Add(
                    $"slice '{entry.Slice}': coveredBy '{entry.CoveredBy}' names no scenario in this model. "
                    + "The cover is a {Feature}/{Scenario} identity the model declares — the feature half defaults "
                    + "to the slice name when `specifications.feature:` is unset.");
            }
        }

        // Several slices may share an owner — that is what part 1's method-level [BobcatSlice] is
        // for — but [BobcatFeature] and [FixtureTitle] are both CLASS-level, so the feature half of
        // every identity in that file is one string. Slices disagreeing about it cannot be written
        // into one type at all.
        var features = model.Slices.ToDictionary(x => x.Name, x => x.Specifications?.Feature ?? x.Name, StringComparer.Ordinal);
        foreach (var group in file.Slices
                     .Where(x => !string.IsNullOrWhiteSpace(x.Owner) && features.ContainsKey(x.Slice))
                     .GroupBy(x => x.Owner!, StringComparer.Ordinal))
        {
            var named = group.Select(x => features[x.Slice]).Distinct(StringComparer.Ordinal).ToList();
            if (named.Count > 1)
            {
                problems.Add(
                    $"owner '{group.Key}' covers slices in more than one feature ({string.Join(", ", named.Select(x => $"'{x}'"))}). "
                    + "The feature is class-level in both authoring styles, so one type cannot publish two of them — "
                    + "split the owner, or give the slices one `specifications.feature:`.");
            }
        }

        return problems;
    }

    /// <summary>Findings that do not invalidate the manifest.</summary>
    public static IReadOnlyList<string> Warn(SpecOwnershipFile file, CuratedModelFile model)
    {
        var warnings = new List<string>();
        var bySlice = model.Slices.ToDictionary(x => x.Name, StringComparer.Ordinal);

        foreach (var entry in file.Slices)
        {
            if (string.IsNullOrWhiteSpace(entry.Slice)) continue;
            if (!bySlice.TryGetValue(entry.Slice, out var slice)) continue;

            // A slice taken out of the Gherkin lane whose scenarios the model still carries: the
            // bodies are now dead weight, since nothing will scaffold them and the projected test
            // states its own arrange/act/assert. Worth saying, because the model is still where
            // the IDENTITIES live and deleting the scenarios outright would break `coveredBy`
            // elsewhere — so the right answer is a judgement call, not a fix this can make.
            if (entry.SuppressesFeature && slice.Specifications is { Scenarios.Count: > 0 })
            {
                warnings.Add(
                    $"slice '{entry.Slice}' is authored as '{entry.ResolvedAuthoring.ToString().ToLowerInvariant()}', so its "
                    + $"{slice.Specifications.Scenarios.Count} scenario body/bodies in the model will not be scaffolded. "
                    + "Their identities still count — keep them if something names them in `coveredBy:`.");
            }

            // A projected test's identity IS its method name (see ProjectedSpecNaming), so a
            // scenario name with punctuation in it silently publishes a different identity.
            if (entry.ResolvedAuthoring == SpecAuthoring.Projected)
            {
                foreach (var scenario in slice.Specifications?.Scenarios ?? [])
                {
                    if (ProjectedSpecNaming.RoundTrips(scenario.Name)) continue;

                    warnings.Add(
                        $"slice '{entry.Slice}', scenario '{scenario.Name}': a projected test's scenario title is its "
                        + $"METHOD NAME with underscores read as spaces, so this one will publish "
                        + $"'{ProjectedSpecNaming.MethodNameFor(scenario.Name).Replace('_', ' ')}' instead and join nothing. "
                        + "Rename it to something a C# method name can spell.");
                }
            }

            if (entry.ResolvedAuthoring == SpecAuthoring.Projected && string.IsNullOrWhiteSpace(entry.Owner))
            {
                warnings.Add(
                    $"slice '{entry.Slice}' is projected but names no `owner:`. Nothing will scaffold it and nothing can "
                    + "check that a test binds it, so the slice is on its own.");
            }
        }

        return warnings;
    }

    /// <summary>
    /// Every <c>{Feature}/{Scenario}</c> the model declares. The feature half defaults to the slice
    /// name, matching how the scaffolder groups features — a feature legally spans slices, so the
    /// two must agree or a valid <c>coveredBy</c> would be reported as naming nothing.
    /// </summary>
    public static IReadOnlySet<string> DeclaredIdentities(CuratedModelFile model)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slice in model.Slices)
        {
            if (slice.Specifications is null) continue;

            var feature = slice.Specifications.Feature ?? slice.Name;
            foreach (var scenario in slice.Specifications.Scenarios)
            {
                identities.Add($"{feature}/{scenario.Name}");
            }
        }

        return identities;
    }
}
