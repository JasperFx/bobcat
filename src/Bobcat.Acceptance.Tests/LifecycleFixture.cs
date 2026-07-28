using Bobcat;
using Bobcat.Engine;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Exercises convention-discovered lifecycle hooks. Nothing here is attributed or overridden —
/// the names alone (<c>BeforeEach</c>, <c>AfterEachAsync</c>, static <c>BeforeAll</c>,
/// static <c>AfterAllAsync</c>) are what the generator discovers.
/// </summary>
public class LifecycleFixture : Fixture
{
    public static int BeforeAllCount;
    public static int AfterAllCount;
    public static int BeforeEachCount;
    public static int AfterEachCount;
    public static bool BeforeAllSawRootSingleton;
    public static readonly List<Guid> SessionsSeenByBeforeEach = new();

    // Instance state — the fixture is constructed fresh per scenario.
    private Guid _sessionSeenByBeforeEach;

    public static void Reset()
    {
        BeforeAllCount = 0;
        AfterAllCount = 0;
        BeforeEachCount = 0;
        AfterEachCount = 0;
        BeforeAllSawRootSingleton = false;
        SessionsSeenByBeforeEach.Clear();
    }

    /// <summary>
    /// Once per feature, before any scenario scope exists — so it may take the context and
    /// root services, but asking for a scoped service here is a compile error (BOBCAT004).
    /// </summary>
    public static void BeforeAll(IStepContext context, [FromRootService] IAppMarker app)
    {
        BeforeAllCount++;
        BeforeAllSawRootSingleton = app != null && context != null;
    }

    public static Task AfterAllAsync()
    {
        AfterAllCount++;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Per scenario, INSIDE the scenario's DI scope — this is the same scoped instance the
    /// steps will see, which is what makes "seed data in BeforeEach" work.
    /// </summary>
    public void BeforeEach(ISessionMarker session)
    {
        BeforeEachCount++;
        _sessionSeenByBeforeEach = session.Id;
        SessionsSeenByBeforeEach.Add(session.Id);
    }

    public Task AfterEachAsync()
    {
        AfterEachCount++;
        return Task.CompletedTask;
    }

    [Check("before-each saw the same scoped session as the step")]
    public bool StepSeesTheSameSession(ISessionMarker session)
        => session.Id == _sessionSeenByBeforeEach;

    [Check("before-all ran exactly once")]
    public bool BeforeAllRanOnce() => BeforeAllCount == 1;

    [Check("before-all resolved the root singleton")]
    public bool BeforeAllResolvedRoot() => BeforeAllSawRootSingleton;

    [Check("before-each has run twice")]
    public bool BeforeEachRanTwice() => BeforeEachCount == 2;

    [Check("each scenario's before-each saw a different session")]
    public bool EachScenarioHadItsOwnSession()
        => SessionsSeenByBeforeEach.Count == 2
           && SessionsSeenByBeforeEach[0] != SessionsSeenByBeforeEach[1];
}
