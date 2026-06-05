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
}

public class ScenarioInfo
{
    public string Title { get; set; } = "";
    public List<string> Tags { get; set; } = new();
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
