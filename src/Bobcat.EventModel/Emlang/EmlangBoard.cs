using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Bobcat.EventModel.Emlang;

/// <summary>The four element kinds of an emlang step, plus the exception marker.</summary>
public enum EmlangElementKind
{
    /// <summary><c>t:</c> — a SCREEN / wireframe trigger.</summary>
    Screen,

    /// <summary><c>c:</c> — a COMMAND.</summary>
    Command,

    /// <summary><c>e:</c> — an EVENT.</summary>
    Event,

    /// <summary><c>v:</c> — a READMODEL / view.</summary>
    View,

    /// <summary><c>x:</c> — a SPEC_ERROR / exception outcome.</summary>
    Error,
}

/// <summary>
/// One step of an emlang chapter. Names arrive as <c>Actor/Label</c> swimlane strings; a name
/// with no slash keeps an empty actor.
/// </summary>
public sealed record EmlangStep(
    EmlangElementKind Kind,
    string Actor,
    string Label,
    IReadOnlyDictionary<string, string> Props);

public sealed record EmlangRef(EmlangElementKind Kind, string Actor, string Label);

/// <summary>A GWT test attached to a chapter: given events, when commands, then events or a read model.</summary>
public sealed record EmlangTest(
    string Name,
    IReadOnlyList<EmlangRef> Given,
    IReadOnlyList<EmlangRef> When,
    IReadOnlyList<EmlangRef> Then);

/// <summary>
/// One emlang chapter — a persona timeline. ⚠️ NOT a slice: a chapter typically contains many
/// slices, and segmenting it into them is <see cref="EmlangImport"/>'s whole job.
/// </summary>
public sealed record EmlangChapter(
    string Name,
    IReadOnlyList<EmlangStep> Steps,
    IReadOnlyList<EmlangTest> Tests);

public sealed record EmlangBoard(IReadOnlyList<EmlangChapter> Chapters);

/// <summary>
/// Parses an emlang YAML export (issue #202). The format is a single top-level <c>slices:</c>
/// map of chapter → { steps, tests }, each step a one-element-key map (<c>t/c/e/v/x</c>) with an
/// optional flat string <c>props</c> map. Parsed into plain records here; every interpretation
/// decision (what is a slice, what is an automation) lives in <see cref="EmlangImport"/> where it
/// is a pure, testable function.
/// </summary>
public static class EmlangReader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    private static readonly IReadOnlyDictionary<string, EmlangElementKind> Keys =
        new Dictionary<string, EmlangElementKind>(StringComparer.Ordinal)
        {
            ["t"] = EmlangElementKind.Screen,
            ["c"] = EmlangElementKind.Command,
            ["e"] = EmlangElementKind.Event,
            ["v"] = EmlangElementKind.View,
            ["x"] = EmlangElementKind.Error,
        };

    public static EmlangBoard Read(string yaml)
    {
        Dictionary<object, object> root;
        try
        {
            root = Deserializer.Deserialize<Dictionary<object, object>>(yaml)
                   ?? throw new EmlangFormatException("the file was empty");
        }
        catch (YamlException e)
        {
            throw new EmlangFormatException($"not parseable as YAML: {e.Message}");
        }

        if (!root.TryGetValue("slices", out var slicesNode) || slicesNode is not Dictionary<object, object> chaptersNode)
        {
            throw new EmlangFormatException("an emlang file has a single top-level `slices:` map of chapters");
        }

        var chapters = new List<EmlangChapter>();
        foreach (var (key, value) in chaptersNode)
        {
            var name = key.ToString() ?? string.Empty;
            if (value is not Dictionary<object, object> chapterNode)
            {
                throw new EmlangFormatException($"chapter '{name}' is not a map of steps/tests");
            }

            chapters.Add(new EmlangChapter(name, readSteps(name, chapterNode), readTests(name, chapterNode)));
        }

        return new EmlangBoard(chapters);
    }

    private static IReadOnlyList<EmlangStep> readSteps(string chapter, Dictionary<object, object> node)
    {
        if (!node.TryGetValue("steps", out var stepsNode)) return [];
        if (stepsNode is not List<object> items)
        {
            throw new EmlangFormatException($"chapter '{chapter}' has a `steps:` that is not a list");
        }

        var steps = new List<EmlangStep>();
        foreach (var item in items)
        {
            if (item is not Dictionary<object, object> step)
            {
                throw new EmlangFormatException($"chapter '{chapter}' has a step that is not a map");
            }

            EmlangElementKind? kind = null;
            var name = string.Empty;
            var props = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (key, value) in step)
            {
                var keyText = key.ToString() ?? string.Empty;
                if (Keys.TryGetValue(keyText, out var elementKind))
                {
                    kind = elementKind;
                    name = value?.ToString() ?? string.Empty;
                }
                else if (keyText == "props" && value is Dictionary<object, object> propsNode)
                {
                    foreach (var (propKey, propValue) in propsNode)
                    {
                        props[propKey.ToString() ?? string.Empty] = propValue?.ToString() ?? string.Empty;
                    }
                }
            }

            if (kind is null)
            {
                throw new EmlangFormatException(
                    $"chapter '{chapter}' has a step with none of the element keys (t/c/e/v/x)");
            }

            var (actor, label) = split(name);
            steps.Add(new EmlangStep(kind.Value, actor, label, props));
        }

        return steps;
    }

    private static IReadOnlyList<EmlangTest> readTests(string chapter, Dictionary<object, object> node)
    {
        if (!node.TryGetValue("tests", out var testsNode)) return [];
        if (testsNode is not Dictionary<object, object> tests)
        {
            throw new EmlangFormatException($"chapter '{chapter}' has a `tests:` that is not a map");
        }

        var result = new List<EmlangTest>();
        foreach (var (key, value) in tests)
        {
            var name = key.ToString() ?? string.Empty;
            if (value is not Dictionary<object, object> test)
            {
                throw new EmlangFormatException($"chapter '{chapter}' test '{name}' is not a map");
            }

            result.Add(new EmlangTest(name, readRefs(test, "given"), readRefs(test, "when"), readRefs(test, "then")));
        }

        return result;
    }

    private static IReadOnlyList<EmlangRef> readRefs(Dictionary<object, object> test, string section)
    {
        if (!test.TryGetValue(section, out var sectionNode) || sectionNode is not List<object> items) return [];

        var refs = new List<EmlangRef>();
        foreach (var item in items)
        {
            if (item is not Dictionary<object, object> reference) continue;

            foreach (var (key, value) in reference)
            {
                var keyText = key.ToString() ?? string.Empty;
                if (!Keys.TryGetValue(keyText, out var kind)) continue;

                var (actor, label) = split(value?.ToString() ?? string.Empty);
                refs.Add(new EmlangRef(kind, actor, label));
            }
        }

        return refs;
    }

    private static (string actor, string label) split(string name)
    {
        var index = name.IndexOf('/');
        return index < 0
            ? (string.Empty, name.Trim())
            : (name[..index].Trim(), name[(index + 1)..].Trim());
    }
}

public sealed class EmlangFormatException(string message) : Exception(message);
