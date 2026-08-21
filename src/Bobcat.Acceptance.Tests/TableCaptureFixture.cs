using Bobcat;
using Bobcat.Engine;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// A <c>[Table]</c> step whose text also carries Cucumber-expression captures (issue #122).
/// The captures bind positionally to the parameters no table column names, exactly as they
/// would on a non-table step; the columns bind by header; anything left is injected.
/// </summary>
public class TableCaptureFixture : Fixture
{
    private readonly List<string> _log = new();

    [Given("these steps ran in {string}")]
    [Table]
    public void StepsRan(string uid, string kind, string text) => _log.Add($"{uid}:{kind}:{text}");

    // Two captures, declared after the column-bound parameter and around an injected one, so
    // the binding is proven to be by role rather than by position in the signature.
    [Given("these {int} rows belong to {string}")]
    [Table]
    public void RowsBelongTo(string label, int count, IStepContext context, string owner)
        => _log.Add($"{owner}/{count}:{label}:{(context == null ? "no-ctx" : "ctx")}");

    [Check("the log is {string}")]
    public bool LogIs(string expected) => string.Join("|", _log) == expected;
}
