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
/// <para>
/// <b>The answer is <see cref="SpecFailureRenderers"/>'s, not this type's.</b> What a library's
/// failure MEANS, and how its message reads, is knowledge that has to be registerable from outside —
/// <c>Bobcat.Xunit</c> contributes xUnit's, a consumer contributes its own. This is the narrow
/// question ("wrong or error?") asked of that registry.
/// </para>
/// </remarks>
public static class ProjectedFailure
{
    /// <summary>The verdict an escaped exception gives the step it escaped from.</summary>
    public static ResultStatus StatusOf(Exception exception)
        => IsAssertion(exception) ? ResultStatus.failed : ResultStatus.error;

    /// <summary>
    /// Whether <paramref name="exception"/> is an assertion disagreeing rather than code
    /// breaking. <see cref="SpecAssertionException"/> says so outright; everything else is decided by
    /// <see cref="SpecFailureRenderers"/> — a registered renderer first, then the naming convention.
    /// </summary>
    /// <remarks>
    /// The whole inheritance chain is walked, so a library's own subclass of its failure type is
    /// recognised through its base. Out of process only one name survives, which is why the registry
    /// and the convention both key on a name rather than a type.
    /// </remarks>
    public static bool IsAssertion(Exception? exception)
    {
        for (var type = exception?.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            if (type == typeof(SpecAssertionException)) return true;
            if (SpecFailureRenderers.IsAssertion(type.Name)) return true;
        }

        return false;
    }

    /// <summary>The same question from a name alone — a verdict that crossed a process boundary.</summary>
    public static bool IsAssertion(string? exceptionTypeName)
        => exceptionTypeName is nameof(SpecAssertionException)
           || SpecFailureRenderers.IsAssertion(exceptionTypeName);
}
