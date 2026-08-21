using System;
using System.Collections.Generic;
using System.Linq;

namespace Bobcat.Generators;

/// <summary>
/// Minimal Gherkin parser that handles the subset Bobcat needs.
/// Avoids the Gherkin NuGet dependency loading issue with source generators.
/// Supports: Feature, Background, Scenario, Scenario Outline + Examples,
/// Given/When/Then/And/But, Data Tables, DocStrings, Tags.
/// Does NOT support: i18n.
/// </summary>
public static class SimpleGherkinParser
{
    public static FeatureInfo? Parse(string content, string filePath)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var feature = new FeatureInfo { FilePath = filePath };

        var background = new List<StepInfo>();
        var pendingTags = new List<string>();

        // Feature-level tags are inherited by every scenario (Gherkin semantics); description
        // lines under Feature: are collected until the first Background/Scenario.
        var featureTags = new List<string>();
        var description = new List<string>();
        var inDescription = false;

        // A scenario's effective tag list: the feature's, then its own, without duplicates.
        List<string> ScenarioTags()
        {
            var tags = new List<string>(featureTags);
            foreach (var tag in pendingTags)
                if (!tags.Contains(tag)) tags.Add(tag);
            return tags;
        }

        // The step list the parser is currently appending to (background, a concrete
        // scenario, or a scenario-outline template).
        List<StepInfo>? currentSteps = null;
        StepInfo? currentStep = null;
        string lastKeyword = "Given";

        // Scenario-outline state (flushed/expanded when the next block or EOF is reached).
        OutlineState? outline = null;
        // Examples table accumulation for the current outline.
        List<string>? exampleHeaders = null;
        List<List<string>>? exampleRows = null;
        var inExamples = false;

        void FlushOutline()
        {
            if (outline != null && exampleHeaders != null && exampleRows != null)
            {
                expandOutline(feature, background, outline, exampleHeaders, exampleRows);
            }
            outline = null;
            exampleHeaders = null;
            exampleRows = null;
            inExamples = false;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();

            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                continue;

            // Tags
            if (trimmed.StartsWith("@"))
            {
                foreach (var tag in trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    if (tag.StartsWith("@")) pendingTags.Add(tag.Substring(1));
                continue;
            }

            // Feature
            if (trimmed.StartsWith("Feature:"))
            {
                feature.Title = trimmed.Substring("Feature:".Length).Trim();
                featureTags.AddRange(pendingTags);
                pendingTags.Clear();
                inDescription = true;
                continue;
            }

            // Background
            if (trimmed.StartsWith("Background:"))
            {
                FlushOutline();
                inDescription = false;
                currentSteps = background;
                currentStep = null;
                lastKeyword = "Given";
                pendingTags.Clear();
                continue;
            }

            // Scenario Outline / Template
            if (trimmed.StartsWith("Scenario Outline:") || trimmed.StartsWith("Scenario Template:"))
            {
                FlushOutline();
                inDescription = false;
                var title = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
                outline = new OutlineState { Title = title, Tags = ScenarioTags() };
                currentSteps = outline.Steps;
                currentStep = null;
                lastKeyword = "Given";
                pendingTags.Clear();
                continue;
            }

            // Scenario
            if (trimmed.StartsWith("Scenario:"))
            {
                FlushOutline();
                inDescription = false;
                var scenario = new ScenarioInfo
                {
                    Title = trimmed.Substring("Scenario:".Length).Trim(),
                    Tags = ScenarioTags()
                };
                // Background steps run first.
                scenario.Steps.AddRange(background.Select(s => s.Clone()));
                feature.Scenarios.Add(scenario);
                currentSteps = scenario.Steps;
                currentStep = null;
                lastKeyword = "Given";
                pendingTags.Clear();
                continue;
            }

            // Examples (belongs to the current outline)
            if (trimmed.StartsWith("Examples:") || trimmed.StartsWith("Scenarios:"))
            {
                inExamples = true;
                currentStep = null;
                continue;
            }

            // DocString — consume until the closing fence.
            if (trimmed == "\"\"\"" || trimmed == "'''")
            {
                var fence = trimmed;
                var indent = raw.Length - raw.TrimStart().Length;
                var docLines = new List<string>();
                i++;
                while (i < lines.Length && lines[i].Trim() != fence)
                {
                    var docLine = lines[i];
                    docLines.Add(docLine.Length >= indent ? docLine.Substring(indent) : docLine.TrimStart());
                    i++;
                }
                if (currentStep != null)
                    currentStep.DocString = string.Join("\n", docLines);
                continue;
            }

            // Table row
            if (trimmed.StartsWith("|"))
            {
                var cells = parseTableRow(trimmed);

                if (inExamples)
                {
                    if (exampleHeaders == null) { exampleHeaders = cells; exampleRows = new List<List<string>>(); }
                    else exampleRows!.Add(cells);
                    continue;
                }

                if (currentStep != null)
                {
                    if (currentStep.TableHeaders == null)
                    {
                        currentStep.TableHeaders = cells;
                        currentStep.TableRows = new List<List<string>>();
                    }
                    else
                    {
                        currentStep.TableRows!.Add(cells);
                    }
                }
                continue;
            }

            // Step keyword
            if (currentSteps != null)
            {
                var step = tryParseStep(trimmed, ref lastKeyword);
                if (step != null)
                {
                    currentSteps.Add(step);
                    currentStep = step;
                    continue;
                }
            }

            // Anything else directly under Feature: is description — "Triggered by …" lives here.
            if (inDescription)
                description.Add(trimmed);
        }

        FlushOutline();

        feature.Tags = featureTags;
        feature.Description = description.Count > 0 ? string.Join("\n", description) : null;

        return feature.Title.Length > 0 ? feature : null;
    }

    private sealed class OutlineState
    {
        public string Title = "";
        public List<string> Tags = new();
        public List<StepInfo> Steps = new();
    }

    private static void expandOutline(FeatureInfo feature, List<StepInfo> background,
        OutlineState outline, List<string> headers, List<List<string>> rows)
    {
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var substitutions = new Dictionary<string, string>();
            for (var c = 0; c < headers.Count && c < row.Count; c++)
                substitutions["<" + headers[c] + ">"] = row[c];

            var scenario = new ScenarioInfo
            {
                Title = $"{outline.Title} [Example {r + 1}]",
                Tags = new List<string>(outline.Tags)
            };

            scenario.Steps.AddRange(background.Select(s => s.Clone()));
            foreach (var template in outline.Steps)
                scenario.Steps.Add(substitute(template, substitutions));

            feature.Scenarios.Add(scenario);
        }
    }

    private static StepInfo substitute(StepInfo template, Dictionary<string, string> substitutions)
    {
        var step = template.Clone();
        step.Text = replace(step.Text, substitutions);
        if (step.DocString != null)
            step.DocString = replace(step.DocString, substitutions);
        if (step.TableHeaders != null)
            step.TableHeaders = step.TableHeaders.Select(h => replace(h, substitutions)).ToList();
        if (step.TableRows != null)
            step.TableRows = step.TableRows.Select(rr => rr.Select(v => replace(v, substitutions)).ToList()).ToList();
        return step;
    }

    private static string replace(string text, Dictionary<string, string> substitutions)
    {
        foreach (var kv in substitutions)
            text = text.Replace(kv.Key, kv.Value);
        return text;
    }

    private static StepInfo? tryParseStep(string line, ref string lastKeyword)
    {
        var keywords = new[] { "Given ", "When ", "Then ", "And ", "But ", "* " };

        foreach (var kw in keywords)
        {
            if (line.StartsWith(kw))
            {
                var keyword = kw.Trim();
                var text = line.Substring(kw.Length).Trim();
                var resolved = keyword;

                if (keyword == "And" || keyword == "But" || keyword == "*")
                    resolved = lastKeyword;
                else
                    lastKeyword = keyword;

                return new StepInfo { Keyword = keyword, ResolvedKeyword = resolved, Text = text };
            }
        }

        return null;
    }

    private static List<string> parseTableRow(string line)
    {
        var cells = new List<string>();
        var trimmed = line.Trim();

        if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1);
        if (trimmed.EndsWith("|")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

        foreach (var part in trimmed.Split('|'))
            cells.Add(part.Trim());

        return cells;
    }
}
