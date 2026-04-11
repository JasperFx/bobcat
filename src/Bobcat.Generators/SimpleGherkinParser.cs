using System;
using System.Collections.Generic;
using System.Linq;

namespace Bobcat.Generators;

/// <summary>
/// Minimal Gherkin parser that handles the subset Bobcat needs.
/// Avoids the Gherkin NuGet dependency loading issue with source generators.
/// Supports: Feature, Scenario, Given/When/Then/And/But, Data Tables, Tags.
/// Does NOT support: Scenario Outline, Background, DocStrings, i18n.
/// </summary>
public static class SimpleGherkinParser
{
    public static FeatureInfo? Parse(string content, string filePath)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var feature = new FeatureInfo { FilePath = filePath };
        ScenarioInfo? currentScenario = null;
        StepInfo? currentStep = null;
        string lastKeyword = "Given";
        var pendingTags = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
            {
                continue;
            }

            // Tags
            if (trimmed.StartsWith("@"))
            {
                var tags = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var tag in tags)
                {
                    if (tag.StartsWith("@"))
                        pendingTags.Add(tag.Substring(1));
                }
                continue;
            }

            // Feature
            if (trimmed.StartsWith("Feature:"))
            {
                feature.Title = trimmed.Substring("Feature:".Length).Trim();
                pendingTags.Clear();
                continue;
            }

            // Scenario
            if (trimmed.StartsWith("Scenario:"))
            {
                currentStep = null;
                currentScenario = new ScenarioInfo
                {
                    Title = trimmed.Substring("Scenario:".Length).Trim(),
                    Tags = new List<string>(pendingTags)
                };
                pendingTags.Clear();
                feature.Scenarios.Add(currentScenario);
                lastKeyword = "Given";
                continue;
            }

            // Background (treat steps as Given)
            if (trimmed.StartsWith("Background:"))
            {
                // TODO: Background support
                continue;
            }

            // Scenario Outline
            if (trimmed.StartsWith("Scenario Outline:") || trimmed.StartsWith("Scenario Template:"))
            {
                // TODO: Scenario Outline support
                continue;
            }

            // Data table row (starts with |)
            if (trimmed.StartsWith("|") && currentStep != null)
            {
                var cells = ParseTableRow(trimmed);
                if (currentStep.TableHeaders == null)
                {
                    currentStep.TableHeaders = cells;
                    currentStep.TableRows = new List<List<string>>();
                }
                else
                {
                    currentStep.TableRows!.Add(cells);
                }
                continue;
            }

            // Step keywords
            if (currentScenario != null)
            {
                var step = TryParseStep(trimmed, ref lastKeyword);
                if (step != null)
                {
                    currentStep = step;
                    currentScenario.Steps.Add(step);
                    continue;
                }
            }
        }

        return feature.Title.Length > 0 ? feature : null;
    }

    private static StepInfo? TryParseStep(string line, ref string lastKeyword)
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
                {
                    resolved = lastKeyword;
                }
                else
                {
                    lastKeyword = keyword;
                }

                return new StepInfo
                {
                    Keyword = keyword,
                    ResolvedKeyword = resolved,
                    Text = text
                };
            }
        }

        return null;
    }

    private static List<string> ParseTableRow(string line)
    {
        var cells = new List<string>();
        var trimmed = line.Trim();

        // Remove leading and trailing |
        if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1);
        if (trimmed.EndsWith("|")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

        var parts = trimmed.Split('|');
        foreach (var part in parts)
        {
            cells.Add(part.Trim());
        }

        return cells;
    }
}
