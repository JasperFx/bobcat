using System.Collections.Generic;
using System.Linq;

namespace Bobcat.Generators;

/// <summary>
/// Compile-time model of a parsed .feature file.
/// </summary>
public class FeatureInfo
{
    public string Title { get; set; } = "";
    public string FilePath { get; set; } = "";
    public List<ScenarioInfo> Scenarios { get; set; } = new();

    /// <summary>Tags on the <c>Feature:</c> line. Every scenario inherits them as well.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Free-text lines between <c>Feature:</c> and the first Background/Scenario, joined with
    /// "\n"; null when there are none. Carries the <c>Triggered by …</c> declaration.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The feature's <c>@arrangement</c> scenarios (issue #259): named lists of Given steps that
    /// never run as tests. By the time parsing returns, every reference to one in
    /// <see cref="Scenarios"/> has already been replaced by its steps — this list survives only so
    /// the generator can report on the arrangements themselves. See <see cref="Arrangements"/>.
    /// </summary>
    public List<ArrangementInfo> Arrangements { get; set; } = new();

    /// <summary>
    /// Why the feature's arrangements could not be expanded, one sentence each; empty when they
    /// could. The generator reports each as BOBCAT022 and emits nothing for the feature.
    /// </summary>
    public List<string> ArrangementProblems { get; set; } = new();
}

public class ScenarioInfo
{
    public string Title { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public List<StepInfo> Steps { get; set; } = new();

    /// <summary>
    /// Free-text lines between <c>Scenario:</c> and the scenario's first step, joined with "\n";
    /// null when there are none. A slice is scenario-level, so its <c>Triggered by …</c> line
    /// belongs here and wins over the feature's (issue #258).
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>A named arrangement: an <c>@arrangement</c> scenario's title and steps, as written.</summary>
public class ArrangementInfo
{
    public string Name { get; set; } = "";
    public List<StepInfo> Steps { get; set; } = new();
}

public class StepInfo
{
    /// <summary>"Given", "When", "Then", "And", "But"</summary>
    public string Keyword { get; set; } = "";
    /// <summary>The resolved keyword (And/But → the previous Given/When/Then)</summary>
    public string ResolvedKeyword { get; set; } = "";
    public string Text { get; set; } = "";
    public List<List<string>>? TableRows { get; set; }
    public List<string>? TableHeaders { get; set; }

    /// <summary>Triple-quoted DocString argument attached to the step, if any.</summary>
    public string? DocString { get; set; }

    /// <summary>Deep copy used when expanding Scenario Outlines and prepending Background.</summary>
    public StepInfo Clone() => new()
    {
        Keyword = Keyword,
        ResolvedKeyword = ResolvedKeyword,
        Text = Text,
        DocString = DocString,
        TableHeaders = TableHeaders == null ? null : new List<string>(TableHeaders),
        TableRows = TableRows?.Select(r => new List<string>(r)).ToList()
    };
}
