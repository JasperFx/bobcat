using System.Text.RegularExpressions;

namespace Bobcat.EventModel.Scaffolding;

/// <summary>
/// A feature whose scenarios repeat the same arranged history, and the arrangements the scaffolder
/// would write for it — the facts a caller shows the user before asking whether to rewrite.
/// </summary>
/// <param name="Feature">The feature file's name.</param>
/// <param name="Arrangements">The arrangement names that would be written, in declaration order.</param>
/// <param name="Scenarios">How many scenarios would reference one instead of restating the history.</param>
public sealed record RepeatedHistory(string Feature, IReadOnlyList<string> Arrangements, int Scenarios);

/// <summary>One named arrangement: its own events, on top of its parent's when it has one.</summary>
public sealed class HistoryArrangement
{
    internal HistoryArrangement(string name, HistoryArrangement? parent, IReadOnlyList<CuratedGiven> events)
    {
        Name = name;
        Parent = parent;
        Events = events;
    }

    /// <summary>The <c>@arrangement</c> scenario's title, and what <c>the arrangement "…"</c> references.</summary>
    public string Name { get; }

    /// <summary>The arrangement this one builds on, referenced as its first step; null for a root.</summary>
    public HistoryArrangement? Parent { get; }

    /// <summary>The events this arrangement adds beyond <see cref="Parent"/>, oldest first.</summary>
    public IReadOnlyList<CuratedGiven> Events { get; }
}

/// <summary>What <see cref="HistoryArrangements.Plan"/> decided for one feature.</summary>
public sealed class HistoryArrangementPlan
{
    private readonly Dictionary<CuratedScenario, (HistoryArrangement Arrangement, int Consumed)> _uses;

    internal HistoryArrangementPlan(IReadOnlyList<HistoryArrangement> arrangements,
        Dictionary<CuratedScenario, (HistoryArrangement, int)> uses)
    {
        Arrangements = arrangements;
        _uses = uses;
    }

    /// <summary>Nothing to arrange — the plan for a feature written longhand.</summary>
    public static HistoryArrangementPlan None { get; } = new([], new());

    /// <summary>The arrangements to declare, parents before the arrangements built on them.</summary>
    public IReadOnlyList<HistoryArrangement> Arrangements { get; }

    /// <summary>How many scenarios reference an arrangement.</summary>
    public int ScenariosUsing => _uses.Count;

    /// <summary>
    /// The arrangement a scenario references, and how many of its leading <c>given:</c> events that
    /// reference stands for — or (null, 0) when it restates its history longhand.
    /// </summary>
    public (HistoryArrangement? Arrangement, int Consumed) For(CuratedScenario scenario)
        => _uses.TryGetValue(scenario, out var use) ? use : (null, 0);
}

/// <summary>
/// Finds arranged history repeated across a feature's scenarios and turns it into named
/// <c>@arrangement</c> scenarios (issue #259). Pure — whether to apply it is the caller's question
/// to ask, because it changes what a regenerated feature looks like.
/// </summary>
/// <remarks>
/// <para>
/// The scenarios' leading <c>given:</c> events form a prefix tree. A node earns an arrangement
/// when at least two scenarios pass through it <b>and</b> they do not all continue into the same
/// child — otherwise the arrangement would be a strictly shorter copy of the one below it that
/// nobody references. On the BookingAppointments chapter that yields exactly the three a person
/// wrote by hand: proposed (10 scenarios), proposed-then-confirmed (5), and
/// proposed-then-confirmed-then-completed (2), each built on the one before.
/// </para>
/// <para>
/// Two events are the same history only when the event type <b>and</b> every <c>with:</c> value
/// agree. And a value carrying <c>{streamId}</c> ends what can be shared, because it expands to a
/// different id in every scenario — an arrangement holding it would put one scenario's stream id
/// into another's history.
/// </para>
/// <para>
/// Names are the events' own, spaced and lower-cased and joined with ", then " — accurate and
/// unique by construction, if not what a person would choose. Renaming one is a find-and-replace
/// in the feature file.
/// </para>
/// </remarks>
public static class HistoryArrangements
{
    public static HistoryArrangementPlan Plan(IEnumerable<CuratedScenario> scenarios)
    {
        var root = new Node(null, "", null);
        var scenarioList = scenarios.ToList();

        foreach (var scenario in scenarioList)
        {
            var node = root;
            foreach (var given in shareable(scenario))
            {
                var key = keyOf(given);
                var child = node.Children.FirstOrDefault(x => x.Key == key);
                if (child is null)
                {
                    child = new Node(node, key, given);
                    node.Children.Add(child);
                }

                child.Count++;
                node = child;
            }
        }

        var arrangements = new List<HistoryArrangement>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        declare(root, null, [], [], arrangements, names);

        var uses = new Dictionary<CuratedScenario, (HistoryArrangement, int)>();
        foreach (var scenario in scenarioList)
        {
            var node = root;
            var depth = 0;
            (HistoryArrangement Arrangement, int Consumed)? deepest = null;

            foreach (var given in shareable(scenario))
            {
                node = node.Children.First(x => x.Key == keyOf(given));
                depth++;
                if (node.Arrangement is { } arrangement) deepest = (arrangement, depth);
            }

            if (deepest is { } use) uses[scenario] = use;
        }

        return arrangements.Count == 0 ? HistoryArrangementPlan.None : new HistoryArrangementPlan(arrangements, uses);
    }

    /// <summary><c>HomeCheckAppointmentProposed</c> → <c>home check appointment proposed</c>.</summary>
    public static string NameFor(string eventName)
        => Regex.Replace(eventName, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").ToLowerInvariant();

    private static void declare(Node node, HistoryArrangement? nearest, List<CuratedGiven> sinceNearest,
        List<string> path, List<HistoryArrangement> arrangements, HashSet<string> names)
    {
        foreach (var child in node.Children)
        {
            var since = new List<CuratedGiven>(sinceNearest) { child.Given! };
            var childPath = new List<string>(path) { NameFor(child.Given!.Event) };

            var everyoneContinuesTogether = child.Children.Count == 1 && child.Children[0].Count == child.Count;
            if (child.Count >= 2 && !everyoneContinuesTogether)
            {
                var arrangement = new HistoryArrangement(unique(string.Join(", then ", childPath), names), nearest, since);
                arrangements.Add(arrangement);
                child.Arrangement = arrangement;
                declare(child, arrangement, [], childPath, arrangements, names);
            }
            else
            {
                declare(child, nearest, since, childPath, arrangements, names);
            }
        }
    }

    private static IEnumerable<CuratedGiven> shareable(CuratedScenario scenario)
        => scenario.Given.TakeWhile(given =>
            !given.With.Values.Any(x => x.Contains(SliceScaffolder.StreamIdToken, StringComparison.OrdinalIgnoreCase)));

    private static string keyOf(CuratedGiven given)
        => given.Event + "|" + string.Join(";", given.With.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key}={x.Value}"));

    private static string unique(string name, HashSet<string> names)
    {
        var candidate = name;
        for (var i = 2; !names.Add(candidate); i++) candidate = $"{name} ({i})";
        return candidate;
    }

    private sealed class Node(Node? parent, string key, CuratedGiven? given)
    {
        public Node? Parent { get; } = parent;
        public string Key { get; } = key;
        public CuratedGiven? Given { get; } = given;
        public int Count { get; set; }
        public List<Node> Children { get; } = [];
        public HistoryArrangement? Arrangement { get; set; }
    }
}
