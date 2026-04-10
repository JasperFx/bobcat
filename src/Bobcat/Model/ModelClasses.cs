using Bobcat.Engine;

namespace Bobcat.Model;

/// <summary>
/// An AST node representing a step in a specification.
/// Steps form a tree: root → children, each referencing an IGrammar.
/// </summary>
public class Step
{
    private readonly List<Step> _children = new();
    private readonly Dictionary<string, object> _values = new();
    private readonly List<Dictionary<string, object>> _rows = new();
    private readonly List<string> _tags = new();

    public IGrammar Grammar { get; }
    public Step? Parent { get; private set; }
    public string Id { get; internal set; }

    public Step(IGrammar grammar, string id)
    {
        Grammar = grammar;
        Id = id;
    }

    public Step Add(IGrammar grammar)
    {
        // Count existing children using the same grammar name for dedup
        var baseName = grammar.Name;
        var existingCount = _children.Count(c => c.Grammar.Name == baseName);
        var suffix = existingCount > 0 ? $".{existingCount + 1}" : "";

        var childId = Id == RootName
            ? $"{baseName}{suffix}"
            : $"{Id}.{baseName}{suffix}";

        var step = new Step(grammar, childId) { Parent = this };
        _children.Add(step);
        return step;
    }

    public Step WithValue(string name, object value)
    {
        _values[name] = value;
        return this;
    }

    public bool TryGetValue(string name, out object value)
    {
        return _values.TryGetValue(name, out value!);
    }

    public Step AddRow(Dictionary<string, object> row)
    {
        _rows.Add(row);
        return this;
    }

    public Step WithTag(string tag)
    {
        _tags.Add(tag);
        return this;
    }

    public IReadOnlyList<Dictionary<string, object>> Rows => _rows;
    public IReadOnlyList<string> Tags => _tags;
    public IReadOnlyList<Step> Children => _children;
    public IReadOnlyDictionary<string, object> Values => _values;

    public static Step ForSpecification(string title)
    {
        return new Step(new SpecificationRoot(title), RootName);
    }

    public const string RootName = "Root";
}

/// <summary>
/// The root grammar of a specification — walks its children to build the execution plan.
/// </summary>
public class SpecificationRoot : IGrammar
{
    public string Title { get; }

    public SpecificationRoot(string title)
    {
        Title = title;
    }

    public string Name { get; } = Step.RootName;

    public void CreatePlan(ExecutionPlan plan, Step step, FixtureInstance fixture)
    {
        foreach (var child in step.Children)
        {
            child.Grammar.CreatePlan(plan, child, fixture);
        }
    }
}
