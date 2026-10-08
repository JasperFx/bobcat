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
/// <param name="Props">Every prop as text: a list reads as its items joined by <c>, </c>, an
/// empty prop as an empty string. <see cref="Values"/> keeps what the YAML actually held.</param>
public sealed record EmlangStep(
    EmlangElementKind Kind,
    string Actor,
    string Label,
    IReadOnlyDictionary<string, string> Props)
{
    /// <summary>
    /// The props as the YAML held them (issue #422): a string, a list of values, a nested map, or
    /// null for a prop left empty. Numbers and booleans arrive as the text they were written as,
    /// which is how YAML hands them over untyped.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Values { get; init; } = EmlangReader.NoValues;
}

/// <summary>
/// An element named by a test's <c>given</c>, <c>when</c> or <c>then</c>, with the example props
/// the test gives it (issue #422). A test's props are sample values, never type names.
/// </summary>
public sealed record EmlangRef(EmlangElementKind Kind, string Actor, string Label)
{
    /// <inheritdoc cref="EmlangStep.Props"/>
    public IReadOnlyDictionary<string, string> Props { get; init; } = EmlangReader.NoProps;

    /// <inheritdoc cref="EmlangStep.Values"/>
    public IReadOnlyDictionary<string, object?> Values { get; init; } = EmlangReader.NoValues;
}

/// <summary>A GWT test attached to a chapter: given events, when commands, then events or a read model.</summary>
public sealed record EmlangTest(
    string Name,
    IReadOnlyList<EmlangRef> Given,
    IReadOnlyList<EmlangRef> When,
    IReadOnlyList<EmlangRef> Then);

/// <summary>
/// One emlang slice as the file writes it, which this importer treats as a chapter — a run of
/// steps it segments. A file slice usually holds one curated slice, but may hold several (a
/// screen, a command and a view), and splitting them is <see cref="EmlangImport"/>'s whole job.
/// </summary>
public sealed record EmlangChapter(
    string Name,
    IReadOnlyList<EmlangStep> Steps,
    IReadOnlyList<EmlangTest> Tests);

public sealed record EmlangBoard(IReadOnlyList<EmlangChapter> Chapters);

/// <summary>
/// Parses an emlang file (issue #202) as the emlang specification writes it and as published
/// models actually use it (issue #422):
/// <list type="bullet">
/// <item>a top-level <c>slices:</c> map, in one YAML document or several separated by
/// <c>---</c>, whose slices are all read in order;</item>
/// <item>a slice written out as <c>steps</c>/<c>tests</c>, or as just its list of steps;</item>
/// <item>each step a map with one element key, short (<c>t/c/e/v/x</c>) or long
/// (<c>trigger/command/event/view/exception</c>), and optional <c>props</c> whose values may be
/// text, numbers, lists, maps or empty;</item>
/// <item><c>given:</c> and <c>then:</c> that may be written and left empty.</item>
/// </list>
/// Parsed into plain records here; every interpretation decision (what is a slice, what is an
/// automation) lives in <see cref="EmlangImport"/> where it is a pure, testable function.
/// </summary>
public static class EmlangReader
{
    internal static readonly IReadOnlyDictionary<string, object?> NoValues = new Dictionary<string, object?>();
    internal static readonly IReadOnlyDictionary<string, string> NoProps = new Dictionary<string, string>();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    private static readonly IReadOnlyDictionary<string, EmlangElementKind> Keys =
        new Dictionary<string, EmlangElementKind>(StringComparer.Ordinal)
        {
            ["t"] = EmlangElementKind.Screen,
            ["trigger"] = EmlangElementKind.Screen,
            ["c"] = EmlangElementKind.Command,
            ["command"] = EmlangElementKind.Command,
            ["e"] = EmlangElementKind.Event,
            ["event"] = EmlangElementKind.Event,
            ["v"] = EmlangElementKind.View,
            ["view"] = EmlangElementKind.View,
            ["x"] = EmlangElementKind.Error,
            ["exception"] = EmlangElementKind.Error,
        };

    public static EmlangBoard Read(string yaml)
    {
        List<Dictionary<object, object>> documents;
        try
        {
            documents = Documents(yaml);
        }
        catch (YamlException e)
        {
            throw new EmlangFormatException($"not parseable as YAML: {e.Message}");
        }

        if (documents.Count == 0) throw new EmlangFormatException("the file was empty");

        var withSlices = documents.Where(x => x.ContainsKey("slices")).ToList();
        if (withSlices.Count == 0)
        {
            throw new EmlangFormatException("an emlang file has a top-level `slices:` map");
        }

        var chapters = new List<EmlangChapter>();
        foreach (var document in withSlices)
        {
            // `slices:` with nothing under it is an empty document, not a malformed one
            if (document["slices"] is null) continue;

            if (document["slices"] is not Dictionary<object, object> slices)
            {
                throw new EmlangFormatException("`slices:` is not a map of slice names");
            }

            foreach (var (key, value) in slices)
            {
                var name = key.ToString() ?? string.Empty;
                chapters.Add(value switch
                {
                    null => new EmlangChapter(name, [], []),

                    // The short form: a slice that is only its steps
                    List<object> steps => new EmlangChapter(name, readSteps(name, steps), []),

                    Dictionary<object, object> chapter => new EmlangChapter(name, readSteps(name, chapter), readTests(name, chapter)),

                    _ => throw new EmlangFormatException(
                        $"slice '{name}' is neither a list of steps nor a map of `steps:`/`tests:`")
                });
            }
        }

        return new EmlangBoard(chapters);
    }

    /// <summary>
    /// Every YAML document in the text that holds a map, in order. A document holding only
    /// comments — the usual preamble before the first <c>---</c> — is skipped.
    /// </summary>
    internal static List<Dictionary<object, object>> Documents(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        parser.Consume<YamlDotNet.Core.Events.StreamStart>();

        var documents = new List<Dictionary<object, object>>();
        while (parser.Accept<YamlDotNet.Core.Events.DocumentStart>(out _))
        {
            var document = Deserializer.Deserialize<object?>(parser);
            switch (document)
            {
                case Dictionary<object, object> map:
                    documents.Add(map);
                    break;
                case null:
                    break;
                default:
                    throw new EmlangFormatException("a YAML document in the file is not a map");
            }
        }

        return documents;
    }

    private static IReadOnlyList<EmlangStep> readSteps(string chapter, Dictionary<object, object> node)
    {
        if (!node.TryGetValue("steps", out var stepsNode) || stepsNode is null) return [];
        if (stepsNode is not List<object> items)
        {
            throw new EmlangFormatException($"slice '{chapter}' has a `steps:` that is not a list");
        }

        return readSteps(chapter, items);
    }

    private static IReadOnlyList<EmlangStep> readSteps(string chapter, List<object> items)
    {
        var steps = new List<EmlangStep>();
        foreach (var item in items)
        {
            if (item is not Dictionary<object, object> step)
            {
                throw new EmlangFormatException($"slice '{chapter}' has a step that is not a map");
            }

            var element = readElement(step)
                          ?? throw new EmlangFormatException(
                              $"slice '{chapter}' has a step with none of the element keys "
                              + "(t/c/e/v/x, or trigger/command/event/view/exception)");

            steps.Add(new EmlangStep(element.Kind, element.Actor, element.Label, element.Props)
            {
                Values = element.Values
            });
        }

        return steps;
    }

    private static IReadOnlyList<EmlangTest> readTests(string chapter, Dictionary<object, object> node)
    {
        if (!node.TryGetValue("tests", out var testsNode) || testsNode is null) return [];
        if (testsNode is not Dictionary<object, object> tests)
        {
            throw new EmlangFormatException($"slice '{chapter}' has a `tests:` that is not a map");
        }

        var result = new List<EmlangTest>();
        foreach (var (key, value) in tests)
        {
            var name = key.ToString() ?? string.Empty;
            var test = value switch
            {
                null => new Dictionary<object, object>(),
                Dictionary<object, object> map => map,
                _ => throw new EmlangFormatException($"slice '{chapter}' test '{name}' is not a map")
            };

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
            if (readElement(reference) is { } element) refs.Add(element);
        }

        return refs;
    }

    private static EmlangRef? readElement(Dictionary<object, object> node)
    {
        EmlangElementKind? kind = null;
        var name = string.Empty;
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in node)
        {
            var keyText = key.ToString() ?? string.Empty;
            if (kind is null && Keys.TryGetValue(keyText, out var elementKind))
            {
                kind = elementKind;
                name = value?.ToString() ?? string.Empty;
            }
            else if (keyText == "props" && value is Dictionary<object, object> propsNode)
            {
                foreach (var (propKey, propValue) in propsNode)
                {
                    var propName = propKey.ToString() ?? string.Empty;
                    values[propName] = propValue;
                    props[propName] = Text(propValue);
                }
            }
        }

        if (kind is null) return null;

        var (actor, label) = split(name);
        return new EmlangRef(kind.Value, actor, label) { Props = props, Values = values };
    }

    /// <summary>A prop value as one line of text: a list joins its items, a map its entries.</summary>
    internal static string Text(object? value) => value switch
    {
        null => string.Empty,
        List<object> items => string.Join(", ", items.Select(Text)),
        Dictionary<object, object> map => string.Join(", ", map.Select(x => $"{x.Key}: {Text(x.Value)}")),
        _ => value.ToString() ?? string.Empty
    };

    private static (string actor, string label) split(string name)
    {
        var index = name.IndexOf('/');
        return index < 0
            ? (string.Empty, name.Trim())
            : (name[..index].Trim(), name[(index + 1)..].Trim());
    }
}

public sealed class EmlangFormatException(string message) : Exception(message);
