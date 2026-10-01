using Bobcat.Engine;

namespace Bobcat;

/// <summary>What a failure is: the system disagreeing, or the code breaking.</summary>
public enum SpecFailureKind
{
    /// <summary>An assertion disagreed — Storyteller's <i>wrong</i>. The scenario carries on.</summary>
    Assertion,

    /// <summary>Something broke. Storyteller's <i>exception</i>.</summary>
    Error
}

/// <summary>
/// A failure as it should be reported: what kind it is, what to say, whether a stack is worth
/// showing, and any expected/actual pairs that could be recovered from it.
/// </summary>
/// <param name="Cells">
/// Structured comparisons parsed out of the failure. Empty for most failures; an assertion library
/// whose message already carries an expected/actual pair can hand them over, and the report then shows
/// a real cell where it would otherwise show a wall of text.
/// </param>
public sealed record SpecFailure(
    SpecFailureKind Kind,
    string Message,
    bool ShowStackTrace,
    IReadOnlyList<CellResult> Cells)
{
    public static SpecFailure Assertion(string message, params CellResult[] cells)
        => new(SpecFailureKind.Assertion, message, ShowStackTrace: false, cells);

    public static SpecFailure Error(string message)
        => new(SpecFailureKind.Error, message, ShowStackTrace: true, []);
}

/// <summary>
/// Everything known about a failure at the point it is rendered.
/// </summary>
/// <remarks>
/// <b>A type NAME, not a type.</b> Out of process a failure is a name and a message and nothing else
/// — xUnit v3 reports <c>ExceptionTypes</c> and <c>ExceptionMessages</c> on
/// <c>TestContext.TestState</c> and never the exception itself, and a supervised worker's failures
/// cross a process boundary. A renderer keyed on <c>Type</c> would work in one lane and not the other,
/// which is the same reason <c>FailureSignature</c> matches names.
/// </remarks>
/// <param name="Exception">The exception itself when there is one — often there is not.</param>
public sealed record SpecFailureContext(
    string TypeName,
    string Message,
    string? StackTrace = null,
    Exception? Exception = null)
{
    public static SpecFailureContext From(Exception exception)
        => new(exception.GetType().Name, exception.Message, exception.StackTrace, exception);
}

/// <summary>
/// Turns one assertion library's failure into a <see cref="SpecFailure"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a plug-in point.</b> Bobcat cannot reference the assertion library a suite happens
/// to use — that is the whole premise of the projected lane — so the knowledge of what
/// <c>ShouldAssertException</c> means, and of how to read its message, has to arrive from outside a
/// hard-coded list. <c>Bobcat.Xunit</c> registers xUnit's; a consumer registers its own house
/// assertion library's in one line.
/// </para>
/// <para>
/// <b>Matched on the name, so it works from the wire too.</b> See
/// <see cref="SpecFailureContext"/>.
/// </para>
/// </remarks>
public interface ISpecFailureRenderer
{
    /// <summary>Whether this renderer knows <paramref name="exceptionTypeName"/>.</summary>
    /// <remarks>
    /// The SIMPLE name, as <c>Type.Name</c> gives it — that is what crosses a process boundary and
    /// what a runner reports. Match a suffix rather than the whole name where a library derives its
    /// own subclasses.
    /// </remarks>
    bool Handles(string exceptionTypeName);

    SpecFailure Render(SpecFailureContext context);
}

/// <summary>
/// The registry of <see cref="ISpecFailureRenderer"/>, and the default reading for everything nobody
/// claims.
/// </summary>
/// <remarks>
/// <para>
/// Registrations are consulted most-recent first, so a consumer can override a built-in without
/// removing it. The default is the last word and always decides.
/// </para>
/// </remarks>
public static class SpecFailureRenderers
{
    private static readonly object _gate = new();

    /// <summary>Most-recently registered first, so a consumer can override a built-in.</summary>
    private static ISpecFailureRenderer[] _registered = [];

    /// <summary>
    /// Assertion-library exception name suffixes the default reading recognises, so a suite using one
    /// of them needs no registration at all to get "a wrong, with its message and no stack".
    /// </summary>
    private static readonly string[] assertionSuffixes =
    [
        "AssertionException",    // NUnit, TUnit, FluentAssertions
        "AssertException",       // Shouldly
        "AssertFailedException", // MSTest
        "XunitException",        // xUnit v2/v3
        "ShouldlyException"
    ];

    static SpecFailureRenderers()
    {
        // Shipped in core rather than in a Bobcat.Shouldly package, because reading the message needs
        // no reference to Shouldly — it is a string, and the shape of it is stable.
        Register(new ShouldlyFailureRenderer());
    }

    /// <summary>
    /// Add a renderer. The most recently registered claimant wins, and disposing the returned handle
    /// removes it again.
    /// </summary>
    /// <remarks>
    /// Reversible because the registry is process-wide: a test that installed one and could not take it
    /// out would change how every later test in the process reads its failures.
    /// </remarks>
    public static IDisposable Register(ISpecFailureRenderer renderer)
    {
        lock (_gate) _registered = [renderer, .._registered];

        return new Registration(renderer);
    }

    /// <summary>Forget every registration, including the built-ins. The test seam.</summary>
    public static void Clear()
    {
        lock (_gate) _registered = [];
    }

    private sealed class Registration(ISpecFailureRenderer renderer) : IDisposable
    {
        public void Dispose()
        {
            lock (_gate) _registered = _registered.Where(x => !ReferenceEquals(x, renderer)).ToArray();
        }
    }

    /// <summary>Whether <paramref name="exceptionTypeName"/> means an assertion disagreed.</summary>
    public static bool IsAssertion(string? exceptionTypeName)
    {
        if (string.IsNullOrEmpty(exceptionTypeName)) return false;

        foreach (var renderer in current)
        {
            if (renderer.Handles(exceptionTypeName!))
            {
                return renderer.Render(new SpecFailureContext(exceptionTypeName!, "")).Kind
                       == SpecFailureKind.Assertion;
            }
        }

        return isAssertionByConvention(exceptionTypeName!);
    }

    /// <summary>How <paramref name="context"/> should be reported.</summary>
    public static SpecFailure Render(SpecFailureContext context)
    {
        foreach (var renderer in current)
        {
            if (renderer.Handles(context.TypeName)) return renderer.Render(context);
        }

        // The default reading, which is what every suite got before this was pluggable: a recognised
        // assertion library is a wrong with its message and no stack, and anything else is an error.
        return isAssertionByConvention(context.TypeName)
            ? SpecFailure.Assertion(context.Message)
            : SpecFailure.Error(context.Message);
    }

    private static ISpecFailureRenderer[] current
    {
        get { lock (_gate) return _registered; }
    }

    /// <summary>The name-suffix convention every mainstream .NET assertion library follows.</summary>
    /// <remarks>
    /// It degrades by OVER-stating severity: an assertion library nobody recognised reports an error,
    /// which is visible and wrong in the safe direction. Reading it as a pass would not be.
    /// </remarks>
    internal static bool isAssertionByConvention(string typeName)
    {
        foreach (var suffix in assertionSuffixes)
        {
            if (typeName.EndsWith(suffix, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}

/// <summary>
/// Shouldly's failures, read as the expected/actual pair they already contain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is worth parsing.</b> A narrated test asserting with Shouldly could only ever report a
/// coarse verdict — the specification showed three grey sentences and a five-line wall of text, with no
/// named cell and no expected/actual anywhere. Shouldly's message already says it:
/// </para>
/// <code>
/// calculator.Value
///     should be
/// 7d
///     but was
/// 6d
/// </code>
/// <para>
/// which is a subject, an expectation and an actual. Parsed, the same failure renders as
/// <c>calculator.Value: expected '7d', got '6d'</c> — a real cell, from a test whose author changed
/// nothing.
/// </para>
/// <para>
/// <b>Never worse than the whole message.</b> Shouldly has many message shapes and this reads one of
/// them; anything it cannot parse falls back to the message verbatim, which is exactly today's output.
/// </para>
/// </remarks>
public sealed class ShouldlyFailureRenderer : ISpecFailureRenderer
{
    public bool Handles(string exceptionTypeName)
        => exceptionTypeName is "ShouldAssertException" or "ShouldlyException"
            or "ShouldMatchApprovedException";

    public SpecFailure Render(SpecFailureContext context)
    {
        var cell = parse(context.Message);

        return cell is null
            ? SpecFailure.Assertion(context.Message)
            : SpecFailure.Assertion(context.Message, cell);
    }

    /// <summary>
    /// <c>subject / "should be" / expected / "but was" / actual</c> → one cell, or null.
    /// </summary>
    private static CellResult? parse(string message)
    {
        var lines = message.Split('\n').Select(x => x.TrimEnd('\r')).ToList();

        var shouldAt = lines.FindIndex(x => x.Trim().StartsWith("should ", StringComparison.Ordinal));
        if (shouldAt <= 0) return null;

        var butAt = lines.FindIndex(shouldAt + 1, x => x.Trim().StartsWith("but was", StringComparison.Ordinal));
        if (butAt < 0) return null;

        var subject = lines[shouldAt - 1].Trim();
        var expected = join(lines, shouldAt + 1, butAt);
        var actual = join(lines, butAt + 1, lines.Count);

        if (subject.Length == 0 || expected.Length == 0 || actual.Length == 0) return null;

        // The name is Shouldly's own subject expression — `calculator.Value` — which is a better cell
        // name than anything Bobcat could invent, and is the whole reason Shouldly's messages read well.
        return new CellResult(subject, ResultStatus.failed) { Expected = expected, Actual = actual };
    }

    private static string join(List<string> lines, int from, int to)
        => string.Join(" ", lines.GetRange(from, Math.Max(0, to - from)).Select(x => x.Trim())
            .Where(x => x.Length > 0));
}
