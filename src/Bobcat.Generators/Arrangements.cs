using System;
using System.Collections.Generic;
using System.Linq;

namespace Bobcat.Generators;

/// <summary>
/// Named arrangements (issue #259): a scenario tagged <c>@arrangement</c> is a named list of Given
/// steps that never runs as a test. A Given step <c>the arrangement "…"</c> references one, and the
/// parser inlines its steps in place — compile time, before step matching, so every inlined step
/// binds, resolves its captures and stamps Event Modeling roles exactly as it would have written out
/// longhand.
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
/// <b>Why a fixed reference phrase rather than the bare name.</b> The first cut inlined any Given
/// whose text was an arrangement's name. <c>And a proposed home check</c> reads best, but no step
/// definition matched it, so VS Code's Cucumber extension underlined every reference as undefined
/// and offered no completion. <see cref="ReferenceExpression"/> is a real step, declared on
/// <c>Bobcat.ArrangementSteps</c> and shipped as source, so the editor completes it like any other
/// step. It removed two rules as well: an arrangement's name can no longer collide with a real
/// step's text, and a misspelled reference is unmistakably a reference — always BOBCAT021, never a
/// generic unmatched step that only an edit-distance guess could explain.
/// </para>
/// <para>
/// <b>The rules, each one closing a way to write a spec that says something it does not do.</b>
/// Given steps only — an arrangement is history, so it can never supply a scenario's act or its
/// assertion (and never the <c>When</c> the Event Model reads as a slice's command). Referenced
/// only from a Given, for the same reason. A reference carries no table or doc string — there is
/// nothing for it to bind to, and dropping it silently would be worse. No cycles and no duplicate
/// names. Any of these is BOBCAT022 and suppresses the feature; a reference naming no arrangement
/// is BOBCAT021 and suppresses it too, because a reference that reached the runner would arrange
/// nothing while saying otherwise.
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
    /// The reference step's Cucumber expression. <c>Bobcat.ArrangementSteps</c> declares the same
    /// text as a literal (the editor's query cannot read a constant); a test pins the two together.
    /// </summary>
    public const string ReferenceExpression = "the arrangement {string}";

    // Case-sensitive, like every Cucumber match: "The arrangement" would be underlined as undefined
    // in the editor, so the generator must not quietly accept what the editor rejects.
    private const string referencePrefix = "the arrangement ";

    /// <summary>
    /// The arrangement name a step references — the quoted text of <c>the arrangement "…"</c>
    /// (double or single quotes, as Cucumber's <c>{string}</c> accepts) — or null when the step is
    /// not a reference.
    /// </summary>
    public static string? ReferencedName(string stepText)
    {
        var text = stepText.Trim();
        if (!text.StartsWith(referencePrefix, StringComparison.Ordinal)) return null;

        var quoted = text.Substring(referencePrefix.Length).Trim();
        if (quoted.Length < 2) return null;

        var quote = quoted[0];
        if ((quote != '"' && quote != '\'') || quoted[quoted.Length - 1] != quote) return null;

        return quoted.Substring(1, quoted.Length - 2);
    }

    /// <summary>
    /// Validate the feature's arrangements and inline every reference to one in its scenarios.
    /// Called once the whole file is parsed, so an arrangement may be declared after its first
    /// use. Leaves <see cref="FeatureInfo.ArrangementProblems"/> or
    /// <see cref="FeatureInfo.UnknownArrangementReferences"/> populated instead of expanding when
    /// something is wrong.
    /// </summary>
    public static void Expand(FeatureInfo feature)
    {
        var anyReference = feature.Scenarios.SelectMany(s => s.Steps)
            .Concat(feature.Arrangements.SelectMany(a => a.Steps))
            .Any(s => ReferencedName(s.Text) != null);

        if (feature.Arrangements.Count == 0 && !anyReference) return;

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
            expand(arrangement.Steps, feature, byName, [arrangement.Name]);
        }

        if (feature.ArrangementProblems.Count > 0 || feature.UnknownArrangementReferences.Count > 0) return;

        foreach (var scenario in feature.Scenarios)
        {
            scenario.Steps = expand(scenario.Steps, feature, byName, []);
        }
    }

    /// <summary>
    /// The declared arrangement a name most plausibly meant — within a small edit distance,
    /// case-insensitive — or null. Feeds BOBCAT021's "did you mean", and the hint for a Given that
    /// writes an arrangement's name bare instead of referencing it.
    /// </summary>
    public static string? NearestName(FeatureInfo feature, string name)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var arrangement in feature.Arrangements)
        {
            var distance = editDistance(arrangement.Name.ToLowerInvariant(), name.Trim().ToLowerInvariant());
            var allowed = Math.Max(2, arrangement.Name.Length / 5);
            if (distance > allowed || distance >= bestDistance) continue;

            best = arrangement.Name;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>The BOBCAT021 sentence for a reference naming no arrangement the feature declares.</summary>
    public static string Describe(FeatureInfo feature, UnknownArrangementReference unknown)
    {
        var help = unknown.Suggestion != null
            ? $"did you mean \"{unknown.Suggestion}\"?"
            : feature.Arrangements.Count == 0
                ? "the feature declares no @arrangement scenarios, and an arrangement is visible only in its own feature file"
                : "the feature declares " + string.Join(", ", feature.Arrangements.Select(a => $"\"{a.Name}\""));

        return $"the step '{unknown.StepText}' references the arrangement \"{unknown.Name}\", which this feature " +
               $"does not declare — {help}";
    }

    private static List<StepInfo> expand(List<StepInfo> steps, FeatureInfo feature,
        Dictionary<string, ArrangementInfo> byName, List<string> path)
    {
        var result = new List<StepInfo>();

        foreach (var step in steps)
        {
            var name = ReferencedName(step.Text);
            if (name == null)
            {
                result.Add(step);
                continue;
            }

            if (!byName.TryGetValue(name, out var arrangement))
            {
                if (feature.UnknownArrangementReferences.All(x => x.StepText != step.Text))
                {
                    feature.UnknownArrangementReferences.Add(new UnknownArrangementReference
                    {
                        StepText = step.Text, Name = name, Suggestion = NearestName(feature, name)
                    });
                }

                continue;
            }

            if (!isGiven(step))
            {
                addOnce(feature.ArrangementProblems,
                    $"the {step.ResolvedKeyword} step '{step.Text}' references the arrangement '{arrangement.Name}' — " +
                    "an arrangement arranges, so reference it from a Given");
                continue;
            }

            if (step.TableHeaders != null || step.DocString != null)
            {
                addOnce(feature.ArrangementProblems,
                    $"the reference to the arrangement '{arrangement.Name}' carries a data table or doc string, " +
                    "which an arrangement has nowhere to put — it would be dropped");
                continue;
            }

            if (path.Contains(arrangement.Name, StringComparer.OrdinalIgnoreCase))
            {
                addOnce(feature.ArrangementProblems,
                    $"the arrangement '{path[0]}' includes itself ({string.Join(" → ", path)} → {arrangement.Name})");
                continue;
            }

            path.Add(arrangement.Name);
            var inlined = expand(arrangement.Steps, feature, byName, path);
            path.RemoveAt(path.Count - 1);

            // The reference's own keyword leads, so "And the arrangement …" still reads as the
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
