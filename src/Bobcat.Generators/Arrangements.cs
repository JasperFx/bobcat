using System;
using System.Collections.Generic;
using System.Linq;

namespace Bobcat.Generators;

/// <summary>
/// Named arrangements (issue #259): a scenario tagged <c>@arrangement</c> is a named list of Given
/// steps that never runs as a test. Wherever another step's text is exactly its name
/// (case-insensitive), the parser inlines its steps in place — compile time, before step matching,
/// so every inlined step binds, resolves its captures and stamps Event Modeling roles exactly as
/// it would have written out longhand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why Gherkin-declared rather than a C# step.</b> A <c>[Given("a proposed home check")]</c>
/// method on the fixture already works today, and it is the right tool when the arrangement is
/// code. It is the wrong one here: it moves the history out of the document the reader has open,
/// the report renders one opaque step where the events used to be, and the scaffolder (which
/// writes features, not fixtures) could never produce one. An <c>@arrangement</c> keeps the
/// history in the feature and in the report.
/// </para>
/// <para>
/// <b>The rules, each one closing a way to write a spec that says something it does not do.</b>
/// Given steps only — an arrangement is history, so it can never supply a scenario's act or its
/// assertion (and never the <c>When</c> the Event Model reads as a slice's command). Referenced
/// only from a Given, for the same reason. A reference carries no table or doc string — there is
/// nothing for it to bind to, and dropping it silently would be worse. No cycles, no duplicate
/// names, and no name that is also the text of a real step (checked by the generator, which has
/// the fixture). Any of these is BOBCAT022 and suppresses the feature.
/// </para>
/// <para>
/// <b>Scope is the feature file.</b> An arrangement is visible exactly where it is used, which is
/// the point of keeping it in Gherkin.
/// </para>
/// </remarks>
public static class Arrangements
{
    public const string Tag = "arrangement";

    /// <summary>
    /// Validate the feature's arrangements and inline every reference to one in its scenarios.
    /// Called once the whole file is parsed, so an arrangement may be declared after its first
    /// use. Leaves <see cref="FeatureInfo.ArrangementProblems"/> populated instead of expanding
    /// when the arrangements themselves are broken.
    /// </summary>
    public static void Expand(FeatureInfo feature)
    {
        if (feature.Arrangements.Count == 0) return;

        var byName = new Dictionary<string, ArrangementInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var arrangement in feature.Arrangements)
        {
            if (byName.ContainsKey(arrangement.Name))
            {
                feature.ArrangementProblems.Add(
                    $"the arrangement '{arrangement.Name}' is declared more than once — a reference could mean either");
                continue;
            }

            byName[arrangement.Name] = arrangement;
        }

        foreach (var arrangement in feature.Arrangements)
        {
            if (arrangement.Steps.Count == 0)
            {
                feature.ArrangementProblems.Add(
                    $"the arrangement '{arrangement.Name}' has no steps, so a reference to it would arrange nothing and say otherwise");
            }

            foreach (var step in arrangement.Steps.Where(s => !isGiven(s)))
            {
                feature.ArrangementProblems.Add(
                    $"the arrangement '{arrangement.Name}' contains the {step.ResolvedKeyword} step '{step.Text}' — " +
                    "an arrangement is history, so it holds Given steps only");
            }

            // Expanding each arrangement once on its own finds a cycle even when nothing refers to it.
            expand(arrangement.Steps, byName, [arrangement.Name], feature.ArrangementProblems);
        }

        if (feature.ArrangementProblems.Count > 0) return;

        foreach (var scenario in feature.Scenarios)
        {
            scenario.Steps = expand(scenario.Steps, byName, [], feature.ArrangementProblems);
        }
    }

    /// <summary>
    /// The arrangement an unmatched step most plausibly meant to name — within a small edit
    /// distance, case-insensitive — or null. This is what turns a misspelled reference into
    /// BOBCAT021 ("did you mean …") instead of the generic unmatched-step BOBCAT002: a reference is
    /// ordinary step text, so a typo in one cannot be told apart from any other unmatched step
    /// except by how close it is to a name the feature declares.
    /// </summary>
    public static string? NearestName(FeatureInfo feature, string stepText)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var arrangement in feature.Arrangements)
        {
            var distance = editDistance(arrangement.Name.ToLowerInvariant(), stepText.Trim().ToLowerInvariant());
            var allowed = Math.Max(2, arrangement.Name.Length / 5);
            if (distance > allowed || distance >= bestDistance) continue;

            best = arrangement.Name;
            bestDistance = distance;
        }

        return best;
    }

    private static List<StepInfo> expand(List<StepInfo> steps, Dictionary<string, ArrangementInfo> byName,
        List<string> path, List<string> problems)
    {
        var result = new List<StepInfo>();

        foreach (var step in steps)
        {
            if (!byName.TryGetValue(step.Text.Trim(), out var arrangement))
            {
                result.Add(step);
                continue;
            }

            if (!isGiven(step))
            {
                addOnce(problems,
                    $"the {step.ResolvedKeyword} step '{step.Text}' names the arrangement '{arrangement.Name}' — " +
                    "an arrangement arranges, so reference it from a Given");
                continue;
            }

            if (step.TableHeaders != null || step.DocString != null)
            {
                addOnce(problems,
                    $"the reference to the arrangement '{arrangement.Name}' carries a data table or doc string, " +
                    "which an arrangement has nowhere to put — it would be dropped");
                continue;
            }

            if (path.Contains(arrangement.Name, StringComparer.OrdinalIgnoreCase))
            {
                addOnce(problems,
                    $"the arrangement '{path[0]}' includes itself ({string.Join(" → ", path)} → {arrangement.Name})");
                continue;
            }

            path.Add(arrangement.Name);
            var inlined = expand(arrangement.Steps, byName, path, problems);
            path.RemoveAt(path.Count - 1);

            // The reference's own keyword leads, so "And a proposed home check" still reads as the
            // continuation it was; the rest continue it.
            for (var i = 0; i < inlined.Count; i++)
            {
                var copy = inlined[i].Clone();
                copy.Keyword = i == 0 ? step.Keyword : "And";
                copy.ResolvedKeyword = "Given";
                result.Add(copy);
            }
        }

        return result;
    }

    private static bool isGiven(StepInfo step)
        => string.Equals(step.ResolvedKeyword.Trim(), "Given", StringComparison.OrdinalIgnoreCase);

    private static void addOnce(List<string> problems, string problem)
    {
        if (!problems.Contains(problem)) problems.Add(problem);
    }

    private static int editDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
