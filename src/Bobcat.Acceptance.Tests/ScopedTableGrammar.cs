using Bobcat;

namespace Bobcat.Acceptance.Tests;

public class TableGrammarScopedFixture : Fixture
{
}

/// <summary>
/// Method injection into the envelope: <c>Before</c> and <c>Row</c> both take the scoped
/// session, and both get the SAME instance because the resource owns one scope per scenario.
/// That identity is what makes a recipe's batched save-once work (issue #39).
/// </summary>
[TableGrammar("the following orders are recorded")]
public class OrderSetupGrammar
{
    public static readonly List<Guid> SessionsSeen = new();
    public static readonly List<string> Recorded = new();

    public static void Reset()
    {
        SessionsSeen.Clear();
        Recorded.Clear();
    }

    public void Before(ISessionMarker session) => SessionsSeen.Add(session.Id);

    public void Row(string reference, ISessionMarker session)
    {
        SessionsSeen.Add(session.Id);
        Recorded.Add(reference);
    }

    public void After(ISessionMarker session) => SessionsSeen.Add(session.Id);
}
