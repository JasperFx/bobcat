using System.Collections.Generic;

namespace Bobcat.Generators;

/// <summary>
/// The spec-ownership join, checked against the compilation (issue #324 part 4).
/// </summary>
/// <remarks>
/// <para>
/// The manifest is the FORWARD declaration — it has to work before any code exists, which is the
/// premise of the pipeline. A spec source's own slice tag is the BACKWARD binding. Checking only
/// the forward direction leaves a manifest that quietly disagrees with the suite; checking only
/// the backward one leaves a slice declared unit-tested that nobody ever wrote a test for. So both.
/// </para>
/// <para>
/// <b>The disagreement that costs something is the LANE.</b> Two sources specifying one slice in
/// two lanes is two specs claiming one identity — the collision the manifest exists to prevent,
/// and the reason separating it from the event model needs a guard at all. The most likely way to
/// get there is not exotic: switch a slice to <c>projected</c>, forget to delete the
/// <c>.feature</c> the scaffolder wrote for it last time, and nothing says a word.
/// </para>
/// <para>
/// <b>Silent without a manifest.</b> A compilation with no manifest in its <c>AdditionalFiles</c>
/// gets no finding from here at all, so adopting the manifest is the opt-in and everything that
/// builds today keeps building.
/// </para>
/// </remarks>
internal static class SpecOwnershipDiagnostics
{
    /// <summary>One slice as some source in this compilation specifies it.</summary>
    internal sealed class Binding
    {
        public string Slice = "";

        /// <summary><c>gherkin</c> | <c>codefirst</c> | <c>projected</c>.</summary>
        public string Lane = "";

        /// <summary>What to name in the message — the feature file, or the test class.</summary>
        public string Source = "";
    }

    internal sealed class Finding
    {
        public bool IsError;
        public string Message = "";
        public string Slice = "";
    }

    public static IReadOnlyList<Finding> Check(
        SpecOwnershipManifest.Manifest manifest, IReadOnlyList<Binding> bindings)
    {
        var findings = new List<Finding>();
        var bound = new HashSet<string>();

        foreach (var binding in bindings)
        {
            bound.Add(binding.Slice);

            var declared = manifest.AuthoringFor(binding.Slice);
            if (declared == binding.Lane) continue;

            // Why the manifest says what it says. With a `defaults:` block (issue #334) an
            // unlisted slice is NOT gherkin, and the old wording would have sent a reader looking
            // for an entry that was never supposed to exist.
            var unlisted = manifest.For(binding.Slice) is not null
                ? ""
                : manifest.Defaults is null
                    ? " (the manifest does not list it, and an unlisted slice is gherkin)"
                    : " (the manifest does not list it, so `defaults:` decide)";

            findings.Add(new Finding
            {
                IsError = true,
                Slice = binding.Slice,
                Message =
                    binding.Source + " specifies slice '" + binding.Slice + "' as " + lane(binding.Lane)
                    + ", but the spec-ownership manifest says " + lane(declared) + unlisted
                    + ". One slice is specified in one place — two lanes means two specs claiming one "
                    + "Feature/Scenario identity."
            });
        }

        foreach (var entry in manifest.Slices)
        {
            var authoring = manifest.AuthoringFor(entry.Slice);
            if (authoring == "gherkin") continue;
            if (bound.Contains(entry.Slice)) continue;

            var named = manifest.OwnerFor(entry.Slice);
            var owner = named is { Length: > 0 } ? " by '" + named + "'" : "";

            findings.Add(new Finding
            {
                IsError = false,
                Slice = entry.Slice,
                Message =
                    "the spec-ownership manifest says slice '" + entry.Slice + "' is specified as "
                    + lane(authoring) + owner
                    + ", but nothing in this compilation binds it. The manifest records a human choice and "
                    + "cannot be derived, so a renamed slice or a deleted test leaves it pointing at nothing."
            });
        }

        return findings;
    }

    /// <summary>
    /// Every slice this compilation specifies, in the lane it specifies it. Scenario tags win over
    /// feature/class tags — a method-level rebinding is a whole binding, the same rule the Event
    /// Model emitter follows.
    /// </summary>
    public static IReadOnlyList<Binding> BindingsIn(
        IEnumerable<FeatureInfo> features,
        IEnumerable<CodeFirstSpecs.SpecInfo> specifications,
        IEnumerable<MarkerCommentSpecs.MarkedSpec> marked)
    {
        var bindings = new List<Binding>();
        var seen = new HashSet<string>();

        void add(string? slice, string lane, string source)
        {
            if (slice is null || slice.Length == 0) return;
            if (!seen.Add(lane + " " + slice + " " + source)) return;

            bindings.Add(new Binding { Slice = slice, Lane = lane, Source = source });
        }

        foreach (var feature in features)
        {
            var name = fileName(feature.FilePath);
            foreach (var scenario in feature.Scenarios)
            {
                var tags = scenario.Tags.Count > 0 ? scenario.Tags : feature.Tags;
                add(GeneratorSliceTags.Slice(tags), "gherkin", name);
            }
        }

        foreach (var spec in specifications)
        {
            foreach (var scenario in spec.Scenarios)
            {
                add(GeneratorSliceTags.Slice(scenario.Tags), "codefirst",
                    "the specification " + spec.FeatureTitle);
            }
        }

        foreach (var spec in marked)
        {
            foreach (var scenario in spec.Scenarios)
            {
                var tags = scenario.Tags.Count > 0 ? scenario.Tags : spec.Tags;
                add(GeneratorSliceTags.Slice(tags), "projected",
                    "the projected test class " + spec.FeatureTitle);
            }
        }

        return bindings;
    }

    private static string lane(string value) => value == "codefirst" ? "code-first" : value;

    private static string fileName(string path)
    {
        var slash = path.LastIndexOfAny(new[] { '/', '\\' });
        return slash < 0 ? path : path.Substring(slash + 1);
    }
}
