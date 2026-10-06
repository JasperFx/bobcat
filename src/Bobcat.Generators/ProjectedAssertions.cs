using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// Projects an ordinary assertion — <c>calculator.Value.ShouldBe(7)</c> — as a specification step,
/// and lets a run of them all be evaluated before the next action.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in per PROJECT</b>, through the <c>BobcatProjectAssertions</c> MSBuild property. Automatic
/// would turn every assertion in every marked class into a step, which is the same over-eagerness the
/// <c>And</c>/<c>But</c> comment rule exists to prevent — and it changes what a test body MEANS, which
/// is not something to switch on for somebody.
/// </para>
/// <para>
/// <b>Only a statement-level call is projected, and that rule is doing real work.</b>
/// <c>x.ShouldNotBeNull().Name.ShouldBe("a")</c> consumes the first assertion's result: gather that
/// one and the chain dereferences null, so the reported failure is a <c>NullReferenceException</c>
/// rather than the assertion that actually failed. A call whose value is used is left completely
/// alone.
/// </para>
/// <para>
/// <b>Shouldly only, for now.</b> Its shape is uniform — the receiver is the subject, the method name
/// is the verb phrase, the arguments are the expectation — so a sentence can be built from the syntax
/// with no guessing. <c>Assert.Equal(expected, actual)</c> is not uniform: the subject is an argument,
/// its position differs per assertion, and a sentence built from the wrong one reads backwards. That
/// needs a per-method table rather than a rule, so it is deliberately not attempted here.
/// </para>
/// </remarks>
internal static class ProjectedAssertions
{
    /// <summary>The MSBuild property, surfaced to the generator as a compiler-visible property.</summary>
    internal const string Property = "build_property.BobcatProjectAssertions";

    /// <summary>Whether this compilation asked for its assertions to be projected.</summary>
    internal static bool Enabled(AnalyzerConfigOptionsProvider config)
        => config.GlobalOptions.TryGetValue(Property, out var value)
           && (value.Equals("true", System.StringComparison.OrdinalIgnoreCase) || value == "1");

    /// <summary>The dialects this generator can read, in the order they are consulted.</summary>
    /// <remarks>
    /// Shouldly is the one Bobcat supports for 1.0. FluentAssertions is the next, and the seam exists so
    /// that is an addition rather than a retrofit — see <see cref="IAssertionDialect"/> for the shape it
    /// will need. <c>Assert.*</c> is <b>not</b> planned: its subject is an argument whose position differs
    /// per assertion, so a sentence built from a rule reads backwards, and a per-method table of every
    /// xUnit assertion is a maintenance burden with no ceiling.
    /// </remarks>
    private static readonly IAssertionDialect[] dialects = [new ShouldlyDialect()];

    /// <summary>The dialect that claims <paramref name="method"/>, or null.</summary>
    internal static IAssertionDialect? DialectFor(IMethodSymbol method)
    {
        foreach (var dialect in dialects)
        {
            if (dialect.Claims(method)) return dialect;
        }

        return null;
    }

    /// <summary>Whether <paramref name="method"/> is an assertion any dialect can project.</summary>
    internal static bool IsAssertion(IMethodSymbol method) => DialectFor(method) != null;

    /// <summary>
    /// The invocation's own statement, when the call stands alone as one — and null when its value is
    /// consumed, which is the case that must never be gathered.
    /// </summary>
    internal static ExpressionStatementSyntax? StatementOf(InvocationExpressionSyntax invocation)
        => invocation.Parent as ExpressionStatementSyntax;

    /// <summary>
    /// Whether this call is the LAST of a maximal run of consecutive assertion statements — the point
    /// at which the run's gathered failures are thrown, just before the next action.
    /// </summary>
    /// <remarks>
    /// Decided from the next sibling statement, so it is a purely local question and each call site can
    /// answer it without seeing the others. A run that ends the block flushes too.
    /// </remarks>
    internal static bool IsLastOfRun(
        ExpressionStatementSyntax statement, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (statement.Parent is not BlockSyntax block) return true;

        var statements = block.Statements;
        var index = statements.IndexOf(statement);
        if (index < 0 || index == statements.Count - 1) return true;

        return !isAssertionStatement(statements[index + 1], model, ct);
    }

    private static bool isAssertionStatement(
        StatementSyntax statement, SemanticModel model, System.Threading.CancellationToken ct)
        => statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax next }
           && model.GetSymbolInfo(next, ct).Symbol is IMethodSymbol method
           && IsAssertion(method);

}

/// <summary>
/// One assertion library's shape: which of its calls are assertions, and how a call reads as a
/// sentence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a seam inside the generator rather than a plug-in.</b> A source generator cannot load a
/// consumer's code, so this cannot be registered the way <c>ISpecFailureRenderer</c> is. What it can be
/// is one small implementation per library, with the library matched by NAMESPACE and name — no
/// reference to it anywhere.
/// </para>
/// <para>
/// <b>FluentAssertions, when it comes.</b> Its shape is <c>x.Should().Be(5)</c>, so:
/// <see cref="Claims"/> matches a method on a type under <c>FluentAssertions</c>; the statement-level
/// call is the LAST one (<c>Be</c>), which is what the existing chaining rule already wants; and
/// <see cref="Subject"/> has to unwrap the receiver's <c>.Should()</c> invocation to reach <c>x</c>
/// rather than taking the receiver expression verbatim. The verb needs the word "should" put back in
/// front, because FluentAssertions spends it on the <c>Should()</c> call — <c>BeGreaterThan</c> reads
/// "should be greater than". Its fluent chains (<c>.And.NotBeNull()</c>) consume the result, so the
/// statement-level rule already leaves them alone, which is the conservative and correct answer.
/// </para>
/// </remarks>
internal interface IAssertionDialect
{
    /// <summary>Whether this dialect owns <paramref name="method"/>.</summary>
    bool Claims(IMethodSymbol method);

    /// <summary>The thing being asserted about, as the author wrote it — the cell's name.</summary>
    string Subject(IMethodSymbol method, InvocationExpressionSyntax invocation);

    /// <summary>The whole claim as a sentence.</summary>
    string Sentence(IMethodSymbol method, InvocationExpressionSyntax invocation);

    /// <summary>
    /// Which of Bobcat's closed comparisons this assertion makes, or <b>null when it makes none
    /// Bobcat has a member for</b> (issue #384).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null is the whitelist doing its job, not a gap: the generator emits <b>no cell at all</b>
    /// for such an assertion and the step renders as a plain line. That is the honest degradation,
    /// and the closed enum is what makes it the only available one — a dialect cannot invent a
    /// comparison the renderer has never heard of.
    /// </para>
    /// <para>
    /// The overload matrix is the second reason to whitelist rather than generalise.
    /// <c>ShouldBeEquivalentTo</c>, <c>ShouldBeOfType</c>, <c>ShouldSatisfyAllConditions</c> and
    /// <c>ShouldThrow</c> all have expectations that are not a value to put beside an actual, and
    /// each would need its own row shape to say anything true.
    /// </para>
    /// </remarks>
    string? ComparisonOf(IMethodSymbol method);
}

/// <summary>
/// Shouldly: <c>calculator.Value.ShouldBe(7)</c> → "calculator.Value should be 7".
/// </summary>
/// <remarks>
/// The shape is uniform, which is why it is the dialect Bobcat supports first: the receiver is the
/// subject, the method name is the verb phrase, and the arguments are the expectation. Nothing has to be
/// guessed at.
/// </remarks>
internal sealed class ShouldlyDialect : IAssertionDialect
{
    public bool Claims(IMethodSymbol method)
        => method.IsExtensionMethod
           && method.Name.StartsWith("Should", System.StringComparison.Ordinal)
           && method.ContainingNamespace?.ToDisplayString() == "Shouldly";

    /// <summary>
    /// The step's sentence, read off the CALL SITE: <c>calculator.Value.ShouldBe(7)</c> becomes
    /// "calculator.Value should be 7".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subject is the receiver expression exactly as the author wrote it, which is the same string
    /// Shouldly reproduces in its failure messages — and Bobcat can read it at compile time rather than
    /// recovering it from source at run time, so it is available whether the assertion fails or not.
    /// </para>
    /// <para>
    /// A trailing <c>customMessage</c> argument is left out: it is an explanation for a failure, not
    /// part of the claim, and reading it into the sentence would put the same words in twice.
    /// </para>
    /// </remarks>
    public string Sentence(IMethodSymbol method, InvocationExpressionSyntax invocation)
    {
        var subject = Subject(method, invocation);

        var verb = Prose(method.Name);
        var arguments = Arguments(method, invocation);

        return arguments.Length == 0 ? $"{subject} {verb}" : $"{subject} {verb} {arguments}";
    }

    /// <summary>
    /// The receiver expression as the author wrote it — <c>calculator.Value</c> — which is the subject
    /// of the sentence AND the cell's name.
    /// </summary>
    public string Subject(IMethodSymbol method, InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax access
            ? access.Expression.ToString()
            : method.Name;

    /// <summary>
    /// Shouldly's method name mapped onto Bobcat's closed comparison set (issue #384).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ShouldBe</c> with a <c>tolerance</c> parameter is <c>Approximately</c> and not
    /// <c>Equals</c> — same method name, different claim, and the one case where the name alone is
    /// not enough. Rendering it as exact equality is the lie the enum exists to make impossible,
    /// and the tolerance itself already travels in the cell's note.
    /// </para>
    /// <para>
    /// Everything absent from this switch deliberately produces <b>no cell</b>:
    /// <c>ShouldBeTrue</c>/<c>ShouldBeFalse</c> (the subject IS the claim, so there is no pair),
    /// <c>ShouldBeEquivalentTo</c>, <c>ShouldBeOfType</c>, <c>ShouldThrow</c>,
    /// <c>ShouldSatisfyAllConditions</c>. The sentence still renders, with a verdict and a
    /// duration; only the cell is withheld, because a cell would have to state a comparison
    /// Bobcat cannot describe.
    /// </para>
    /// </remarks>
    public string? ComparisonOf(IMethodSymbol method)
        => ComparisonFor(method.Name, method.Parameters.Any(p => p.Name is "tolerance"));

    /// <summary>
    /// The mapping itself, as a pure function of the two facts it needs — so the whitelist can be
    /// read and tested without a compilation, exactly as <see cref="Prose"/> is.
    /// </summary>
    internal static string? ComparisonFor(string methodName, bool hasTolerance)
    {
        return methodName switch
        {
            "ShouldBe" => hasTolerance ? "Approximately" : "Equals",
            "ShouldNotBe" => "NotEquals",
            "ShouldBeGreaterThan" => "GreaterThan",
            "ShouldBeGreaterThanOrEqualTo" => "GreaterThanOrEqual",
            "ShouldBeLessThan" => "LessThan",
            "ShouldBeLessThanOrEqualTo" => "LessThanOrEqual",
            "ShouldContain" => "Contains",
            "ShouldStartWith" => "StartsWith",
            "ShouldEndWith" => "EndsWith",
            "ShouldBeNull" => "IsNull",
            "ShouldNotBeNull" => "IsNotNull",
            "ShouldBeEmpty" => "IsEmpty",
            "ShouldNotBeEmpty" => "IsNotEmpty",
            _ => null
        };
    }

    /// <summary><c>ShouldBeGreaterThan</c> → "should be greater than".</summary>
    internal static string Prose(string methodName)
    {
        var words = new StringBuilder();

        for (var i = 0; i < methodName.Length; i++)
        {
            if (i > 0 && char.IsUpper(methodName[i])) words.Append(' ');
            words.Append(char.ToLowerInvariant(methodName[i]));
        }

        return words.ToString();
    }

    private static string Arguments(IMethodSymbol method, InvocationExpressionSyntax invocation)
    {
        var written = invocation.ArgumentList.Arguments;
        var texts = new List<string>();

        for (var i = 0; i < written.Count; i++)
        {
            // `method` here is the REDUCED extension method, whose parameters already exclude the
            // receiver — so written[i] lines up with Parameters[i]. Indexing i + 1 skipped the real
            // argument and read the custom message instead, which dropped the expected value out of
            // every sentence: "calculator.Value should be".
            var parameter = i < method.Parameters.Length ? method.Parameters[i] : null;
            if (parameter != null && parameter.Name is "customMessage" or "customMessageFunc") continue;

            texts.Add(written[i].Expression.ToString());
        }

        return string.Join(", ", texts);
    }
}
