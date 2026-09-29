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

    /// <summary>
    /// Whether <paramref name="method"/> is an assertion this can project: a Shouldly extension method.
    /// </summary>
    /// <remarks>
    /// Matched by NAMESPACE and name, never by symbol identity — Bobcat references no assertion
    /// library, which is the premise of the whole projected lane.
    /// </remarks>
    internal static bool IsAssertion(IMethodSymbol method)
        => method.IsExtensionMethod
           && method.Name.StartsWith("Should", System.StringComparison.Ordinal)
           && method.ContainingNamespace?.ToDisplayString() == "Shouldly";

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
    internal static string Sentence(IMethodSymbol method, InvocationExpressionSyntax invocation)
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
    internal static string Subject(IMethodSymbol method, InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax access
            ? access.Expression.ToString()
            : method.Name;

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
