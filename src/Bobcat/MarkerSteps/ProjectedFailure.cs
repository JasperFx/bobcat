using Bobcat.Engine;

namespace Bobcat;

/// <summary>
/// How a projected test's exception becomes a step verdict: a <b>wrong</b> (the assertion
/// disagreed) or an <b>error</b> (something blew up).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the distinction is worth keeping here.</b> It is the same split the MTP host already
/// honours between <c>failed</c> and <c>error</c>, and the split a supervisor's
/// <c>Disposition</c> policy keys off — and it is the one Storyteller counted as <i>wrongs</i>
/// against <i>exceptions</i>. Collapsing it would make a wrong answer and a crash render
/// identically, which is the one thing a reader of a spec report most needs to tell apart.
/// </para>
/// <para>
/// <b>Matched by type NAME, walking the inheritance chain.</b> Bobcat cannot reference the
/// assertion library a projected suite happens to use — that is the whole point of the projected
/// lane — so the only thing available is the name. The same rule <c>FailureSignature</c> lives by,
/// and it degrades the same way: an unrecognised assertion library reports <c>error</c>, which
/// over-states the severity of a failure rather than under-stating it.
/// </para>
/// </remarks>
public static class ProjectedFailure
{
    /// <summary>
    /// Type-name suffixes every mainstream .NET assertion library ends its failure exception
    /// with. Matched as a suffix so a library's own subclasses come along for free.
    /// </summary>
    private static readonly string[] assertionSuffixes =
    [
        "AssertionException",   // NUnit, TUnit, FluentAssertions' own
        "AssertException",      // Shouldly (ShouldAssertException)
        "AssertFailedException",// MSTest
        "XunitException",       // xUnit v2/v3 — Assert.* and Record.Exception
        "ShouldlyException"
    ];

    /// <summary>The verdict an escaped exception gives the step it escaped from.</summary>
    public static ResultStatus StatusOf(Exception exception)
        => IsAssertion(exception) ? ResultStatus.failed : ResultStatus.error;

    /// <summary>
    /// Whether <paramref name="exception"/> is an assertion disagreeing rather than code
    /// breaking. <see cref="SpecAssertionException"/> says so outright; everything else is
    /// recognised by the naming convention its library follows.
    /// </summary>
    public static bool IsAssertion(Exception? exception)
    {
        for (var type = exception?.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            if (type == typeof(SpecAssertionException)) return true;

            var name = type.Name;
            foreach (var suffix in assertionSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal)) return true;
            }
        }

        return false;
    }
}
