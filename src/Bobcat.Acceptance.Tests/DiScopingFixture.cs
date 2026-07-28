using Bobcat;

namespace Bobcat.Acceptance.Tests;

/// <summary>A scoped service — a stand-in for Marten's IDocumentSession or an EF DbContext.</summary>
public interface ISessionMarker
{
    Guid Id { get; }
}

public class SessionMarker : ISessionMarker
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>A singleton — must resolve identically from the root container and the scope.</summary>
public interface IAppMarker
{
    Guid Id { get; }
}

public class AppMarker : IAppMarker
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>
/// Exercises the per-scenario DI scope. Non-simple parameter types are resolved from the
/// scenario scope by convention; <c>[FromRootService]</c>, <c>[NewScope]</c>, and
/// <c>[ScopePerRow]</c> are the explicit overrides.
/// </summary>
public class DiScopingFixture : Fixture
{
    /// <summary>Every scoped id seen across the whole feature run — the cross-scenario view.</summary>
    public static readonly List<Guid> AllScenarioCaptures = new();

    // The fixture is constructed fresh per scenario, so instance state is per-scenario.
    private readonly List<Guid> _captures = new();
    private readonly List<Guid> _nested = new();
    private readonly List<Guid> _rows = new();

    public static void Reset() => AllScenarioCaptures.Clear();

    [Given("the scoped session is captured")]
    public void Capture(ISessionMarker session)
    {
        _captures.Add(session.Id);
        AllScenarioCaptures.Add(session.Id);
    }

    [When("the scoped session is captured again")]
    public void CaptureAgain(ISessionMarker session) => _captures.Add(session.Id);

    [Check("both captures are the same instance")]
    public bool BothCapturesSame() => _captures.Count == 2 && _captures[0] == _captures[1];

    [Check("the singleton is the same as the root service")]
    public bool SingletonMatchesRoot(IAppMarker scoped, [FromRootService] IAppMarker root)
        => scoped.Id == root.Id;

    [Check("the scoped session differs from the previous scenario")]
    public bool DiffersFromPreviousScenario()
        => AllScenarioCaptures.Count == 2 && AllScenarioCaptures[0] != AllScenarioCaptures[1];

    [When("a nested-scope step captures the session")]
    [NewScope]
    public void CaptureNested(ISessionMarker session) => _nested.Add(session.Id);

    [Check("the nested capture differs from the scenario capture")]
    public bool NestedDiffers()
        => _nested.Count == 1 && _captures.Count == 1 && _nested[0] != _captures[0];

    [When("each of these rows captures the session")]
    [Table]
    [ScopePerRow]
    public void CaptureRow(string label, ISessionMarker session) => _rows.Add(session.Id);

    [Check("every row captured a different instance")]
    public bool RowsAreDistinct()
        => _rows.Count == 2 && _rows.Distinct().Count() == 2 && !_rows.Contains(_captures[0]);
}
