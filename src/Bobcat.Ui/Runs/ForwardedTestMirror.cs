namespace Bobcat.Ui.Runs;

/// <summary>
/// Decides which of the supervisor's forwarded tests are the SAME test a Bobcat worker already
/// published as a scenario, so a supervised projected run is counted once, not twice.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a uid guard is not enough.</strong> A supervised Bobcat suite puts two streams on
/// the wire for every test: the worker's own <c>scenario_*</c> events, keyed by the spec identity
/// <c>{Feature}/{Scenario}</c>, and the supervisor's forwarded <c>test_*</c> pair (issue #195),
/// keyed by the WORKER's test id. The fold's <c>WorkerPublished</c> guard assumed those two keys
/// were one string, which was true of the Gherkin runner, whose MTP node uid was the spec identity.
/// In the projected lane the MTP uid is xUnit's hash of the test method
/// (<c>cbdeeef1d7f2…</c>), so the guard never fired and the rehearsal's 19 tests rendered as 38
/// scenarios (2026-10-12, Bobcat 0.60.1, <c>SupervisorRunPublisher.TestUpdated</c>).
/// </para>
/// <para>
/// <strong>The join is by name, because the wire carries nothing better.</strong> A forwarded
/// test's display name is the test method's full name, <c>Ns.class_name.method_name</c>, and a
/// projected spec's scenario title IS its method name with the underscores read as spaces. So a
/// forwarded test mirrors the worker scenario whose title matches its method; when more than one
/// worker scenario has that title, the class must also match the feature. Comparison is on letters
/// and digits only, case-insensitive, which also carries a <c>[BobcatSpec(Name = "...")]</c> title
/// that only adds punctuation (<c>a node's claim</c> beside <c>a_nodes_claim</c>).
/// </para>
/// <para>
/// <strong>What it refuses to do.</strong> It never hides a forwarded test with no match — a plain
/// <c>[Fact]</c> beside the specs in a spec assembly has no worker scenario and stays visible,
/// failing or not. And it never guesses between two equally good matches. A miss costs a duplicate
/// row; a wrong match would hide a real test. Read-time rather than fold-time, so it holds in
/// either arrival order and on archive replay. The Pinia store mirrors this rule
/// (<c>visibleScenarios</c> in <c>runs-store.ts</c>).
/// </para>
/// </remarks>
public static class ForwardedTestMirror
{
    /// <summary>The scenarios a reader should see: every one, less the forwarded mirrors.</summary>
    public static IReadOnlyList<ScenarioProjection> Visible(IReadOnlyCollection<ScenarioProjection> scenarios)
    {
        var published = scenarios.Where(s => s.WorkerPublished).ToList();
        if (published.Count == 0) return scenarios.ToList();

        var byTitle = published
            .GroupBy(s => KeyOf(s.Scenario))
            .ToDictionary(g => g.Key, g => g.ToList());

        return scenarios
            .Where(s => !(s.Forwarded && !s.WorkerPublished && mirrors(s, byTitle)))
            .ToList();
    }

    private static bool mirrors(ScenarioProjection forwarded, Dictionary<string, List<ScenarioProjection>> byTitle)
    {
        if (forwarded.DisplayName is not { } displayName) return false;
        var (className, method) = Split(displayName);
        if (!byTitle.TryGetValue(KeyOf(method), out var candidates)) return false;
        if (candidates.Count == 1) return true;

        var classKey = KeyOf(className);
        return classKey.Length > 0 && candidates.Count(c => KeyOf(c.Feature) == classKey) == 1;
    }

    /// <summary>
    /// <c>Ns.Class.method_name(x: 1)</c> → (<c>Class</c>, <c>method_name</c>). The argument list is
    /// cut first: a theory's arguments may contain dots of their own.
    /// </summary>
    public static (string ClassName, string Method) Split(string displayName)
    {
        var paren = displayName.IndexOf('(');
        var name = paren >= 0 ? displayName[..paren] : displayName;
        var segments = name.Split('.');
        var method = segments[^1];
        var className = segments.Length > 1 ? segments[^2] : "";
        return (className, method);
    }

    /// <summary>Letters and digits only, lower-cased: the part of a name both spellings share.</summary>
    public static string KeyOf(string? text)
        => text is null ? "" : new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
