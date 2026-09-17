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

        validateDefaults(file, problems);

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

            var resolved = file.Resolve(entry.Slice);

            // The one impossible corner of two otherwise orthogonal axes. A rule and not a
            // comment: both Gherkin and code-first run through the fixture and therefore the
            // store, so `unit` with either is a contradiction, and silently honouring the
            // authoring would hand back a `.feature` to an author who asked for a unit test.
            if (resolved.Kind == SpecKind.Unit && resolved.Authoring != SpecAuthoring.Projected)
            {
                problems.Add(
                    $"slice '{entry.Slice}': kind 'unit' cannot be authored as '{resolved.Authoring.ToString().ToLowerInvariant()}' — both gherkin and "
                    + "code-first run through the fixture and so through the store. A unit-tested slice is `authoring: projected`.");
            }

            if (resolved.Kind == SpecKind.Unit && string.IsNullOrWhiteSpace(resolved.CoveredBy))
            {
                problems.Add(
                    $"slice '{entry.Slice}': `coveredBy:` is required for a unit-tested slice — name the "
                    + "{Feature}/{Scenario} that runs this slice's command end to end.");
            }

            // Issue #334: the corner the format refuses to guess in. Reported per slice, because
            // the answer is per slice — one repo legitimately has both an adopted suite and slices
            // it is still building.
            if (resolved.NeedsScaffoldStated && entry.Scaffold is null && file.Defaults?.Scaffold is null)
            {
                problems.Add(
                    $"slice '{entry.Slice}': a projected integration slice must say `scaffold:`. `true` writes the "
                    + "skeleton — the class, the [BobcatSlice] binding and one method per scenario, named exactly as "
                    + "the model names them; `false` means an existing suite already covers this slice and nothing is "
                    + "generated. Nothing can tell those apart: the scaffolder sees neither the compilation nor the disk.");
            }
        }

        foreach (var group in file.Slices
                     .Where(x => !string.IsNullOrWhiteSpace(x.Owner))
                     .GroupBy(x => x.Owner!, StringComparer.Ordinal))
        {
            var styles = group.Select(x => file.Resolve(x.Slice).Authoring).Distinct().ToList();
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
    /// The file-level <c>defaults:</c> block (issue #334). Judged on its own terms, plus the one
    /// question it can answer for every slice at once: whether it puts unlisted slices in the
    /// corner where <c>scaffold:</c> has to be stated.
    /// </summary>
    private static void validateDefaults(SpecOwnershipFile file, List<string> problems)
    {
        if (file.Defaults is not { } defaults) return;

        validateEnumValue<SpecKind>(defaults.Kind, "defaults kind", SpecOwnershipVocabulary.KindNames, problems);
        validateEnumValue<SpecAuthoring>(defaults.Authoring, "defaults authoring", SpecOwnershipVocabulary.AuthoringNames, problems);

        foreach (var token in SpecOwnershipDefaults.UnknownOwnerTokens(defaults.Owner))
        {
            problems.Add(
                $"`defaults.owner:` uses the token '{{{token}}}', which is not one of: "
                + $"{string.Join(" | ", SpecOwnershipDefaults.OwnerTokens.Select(x => $"{{{x}}}"))}. "
                + "An unrecognized token would be left in a type name.");
        }

        if (defaults.StatedKind == SpecKind.Unit)
        {
            problems.Add(
                "`defaults.kind: unit` is not allowed — a unit-tested slice must name the `coveredBy:` scenario that "
                + "runs its command end to end, which is per slice by nature. State `kind: unit` on the slices that are.");
        }

        // An all-projected repo's whole point is that most slices have no entry at all, so the
        // defaults themselves land in the ambiguous corner and have to answer for them.
        var unlisted = file.Resolve("\u0000not-a-slice");
        if (unlisted.NeedsScaffoldStated && defaults.Scaffold is null)
        {
            problems.Add(
                "`defaults:` make every slice a projected integration slice, so `defaults.scaffold:` must say whether "
                + "the scaffolder writes them. `true` for a repo being built; `false` when existing suites adopt the "
                + "slices. Slices that differ say so on their own entry.");
        }
    }

    private static void validateEnumValue<TEnum>(
        string? value, string where, IReadOnlyList<string> names, List<string> problems) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var known = typeof(TEnum) == typeof(SpecKind)
            ? SpecOwnershipVocabulary.TryParseKind(value, out _)
            : SpecOwnershipVocabulary.TryParseAuthoring(value, out _);

        if (!known) problems.Add($"{where} '{value}' is not one of: {string.Join(" | ", names)}.");
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
        //
        // Checked over the MODEL's slices and their RESOLVED owners since #334, because that is
        // where a default owner template can go wrong: `defaults.owner: X.Specs.AllSpecs` — a
        // literal, with no {feature} token — quietly points nineteen slices across six features at
        // one type, which is the same collision a hand-written owner would be caught for.
        foreach (var group in model.Slices
                     .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                     .Select(x => (Slice: x, Feature: x.Specifications?.Feature ?? x.Name))
                     .Select(x => (x.Slice, x.Feature, Resolved: file.Resolve(x.Slice.Name, x.Feature)))
                     .Where(x => x.Resolved.Owner is { Length: > 0 })
                     .GroupBy(x => x.Resolved.Owner!, StringComparer.Ordinal))
        {
            var named = group.Select(x => x.Feature).Distinct(StringComparer.Ordinal).ToList();
            if (named.Count > 1)
            {
                var how = group.Any(x => x.Resolved.Listed && file.EntryFor(x.Slice.Name)?.Owner is { Length: > 0 })
                    ? ""
                    : " (from `defaults.owner:`)";

                problems.Add(
                    $"owner '{group.Key}'{how} covers slices in more than one feature ({string.Join(", ", named.Select(x => $"'{x}'"))}). "
                    + "The feature is class-level in both authoring styles, so one type cannot publish two of them — "
                    + "split the owner, or give the slices one `specifications.feature:`.");
            }
        }

        return problems;
    }

    /// <summary>Findings that do not invalidate the manifest.</summary>
    /// <remarks>
    /// Walks the MODEL's slices rather than this file's entries (issue #334): with a
    /// <c>defaults:</c> block most slices have no entry at all, and the warnings worth having —
    /// above all the projected method-name check — are about exactly those.
    /// </remarks>
    public static IReadOnlyList<string> Warn(SpecOwnershipFile file, CuratedModelFile model)
    {
        var warnings = new List<string>();

        foreach (var slice in model.Slices)
        {
            if (string.IsNullOrWhiteSpace(slice.Name)) continue;

            var feature = slice.Specifications?.Feature ?? slice.Name;
            var resolved = file.Resolve(slice.Name, feature);

            // A slice taken out of the Gherkin lane that nothing will scaffold, whose scenarios
            // the model still carries: the bodies are dead weight, since the adopting suite states
            // its own arrange/act/assert. Worth saying, because the model is still where the
            // IDENTITIES live and deleting the scenarios outright would break `coveredBy`
            // elsewhere — so the right answer is a judgement call, not a fix this can make.
            //
            // Since #334 this fires only for a slice nothing is generated for. A projected slice
            // the scaffolder DOES write turns those same scenarios into method names and step
            // comments, so warning about them was the finding that produced 19 near-identical
            // lines on an all-projected repo.
            if (resolved.SuppressesFeature && !resolved.Scaffold
                && slice.Specifications is { Scenarios.Count: > 0 })
            {
                warnings.Add(
                    $"slice '{slice.Name}' is authored as '{resolved.Authoring.ToString().ToLowerInvariant()}' with "
                    + $"`scaffold: false`, so its {slice.Specifications.Scenarios.Count} scenario body/bodies in the "
                    + "model will not be scaffolded. Their identities still count — keep them if something names them "
                    + "in `coveredBy:`.");
            }

            // A projected test's identity IS its method name (see ProjectedSpecNaming), so a
            // scenario name with punctuation in it silently publishes a different identity.
            if (resolved.Authoring == SpecAuthoring.Projected)
            {
                foreach (var scenario in slice.Specifications?.Scenarios ?? [])
                {
                    if (ProjectedSpecNaming.RoundTrips(scenario.Name)) continue;

                    warnings.Add(
                        $"slice '{slice.Name}', scenario '{scenario.Name}': a projected test's scenario title is its "
                        + $"METHOD NAME with underscores read as spaces, so this one will publish "
                        + $"'{ProjectedSpecNaming.MethodNameFor(scenario.Name).Replace('_', ' ')}' instead and join nothing. "
                        + "Rename it to something a C# method name can spell.");
                }
            }

            if (resolved.Authoring == SpecAuthoring.Projected && string.IsNullOrWhiteSpace(resolved.Owner))
            {
                warnings.Add(
                    $"slice '{slice.Name}' is projected but names no `owner:`, and `defaults.owner:` gives it none. "
                    + "Nothing will scaffold it and nothing can check that a test binds it, so the slice is on its own.");
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
