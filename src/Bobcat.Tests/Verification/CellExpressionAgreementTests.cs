using Bobcat.Engine;
using Bobcat.Engine.Verification;
using Bobcat.Generators;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Verification;

/// <summary>
/// The generator decides at compile time whether a cell is an expression; <see cref="CellValues"/>
/// decides the same thing at run time, from its own copy, because Bobcat.Generators is netstandard2.0
/// and references nothing.
/// </summary>
/// <remarks>
/// The agreement that matters runs one way: every cell the runtime can resolve must be one the
/// generator admits. The other direction degrades to a run-time failure naming the cell; this
/// direction degrades to a build error over legal input, which an author cannot work around.
/// </remarks>
public class CellExpressionAgreementTests
{
    public static TheoryData<string> RelativeTokens =>
    [
        "TODAY", "today", " TODAY ", "NOW", "now",
        "TODAY+1", "TODAY-1", "TODAY+365", "TODAY - 1 week", "TODAY + 2 days",
        "NOW + 30 minutes", "NOW - 2 hours", "NOW+1h", "NOW - 1d2h30m"
    ];

    [Theory]
    [MemberData(nameof(RelativeTokens))]
    public void everything_the_runtime_resolves_the_generator_admits(string token)
    {
        // Resolvable at run time...
        RelativeTimeResolver.TryResolve(token, TimeProvider.System, out _, out _).ShouldBeTrue();

        // ...so the build must not refuse it.
        CellExpressions.IsRelativeTime(token).ShouldBeTrue();
        CellValues.IsRelativeTime(token).ShouldBeTrue();
    }

    [Theory]
    [InlineData("NOW+5")]
    [InlineData("NOW-5")]
    public void a_cell_that_looks_relative_and_resolves_to_nothing_fails_at_run_time_naming_it(string token)
    {
        // The documented asymmetry, in the safe direction. A bare number after NOW says five of
        // WHAT — days after TODAY, and nothing after NOW — so the shape test admits it and the
        // resolver refuses it. That is a legible run-time failure, not a build error over legal input.
        CellExpressions.IsRelativeTime(token).ShouldBeTrue();
        RelativeTimeResolver.TryResolve(token, TimeProvider.System, out _, out _).ShouldBeFalse();

        var ex = Should.Throw<SpecCriticalException>(() => CellValues.Read<DateTime>(token));
        ex.Message.ShouldContain(token);
    }

    public static TheoryData<string> Literals => ["2026-01-01", "14:30", "", "Yesterday", "TOMORROW", "5"];

    [Theory]
    [MemberData(nameof(Literals))]
    public void a_literal_is_not_read_as_an_expression_by_either_side(string text)
    {
        CellExpressions.IsRelativeTime(text).ShouldBeFalse();
        CellValues.IsRelativeTime(text).ShouldBeFalse();
    }

    [Fact]
    public void both_sides_agree_which_types_a_relative_time_can_be_read_as()
    {
        CellExpressions.IsTemporal("System.DateTime").ShouldBeTrue();
        CellExpressions.IsTemporal("System.DateTimeOffset").ShouldBeTrue();
        CellExpressions.IsTemporal("System.DateOnly").ShouldBeTrue();
        CellExpressions.IsTemporal("System.TimeOnly").ShouldBeTrue();
        CellExpressions.IsTemporal("System.TimeSpan").ShouldBeFalse();
        CellExpressions.IsTemporal("string").ShouldBeFalse();

        CellValues.IsTemporalType(typeof(DateTime)).ShouldBeTrue();
        CellValues.IsTemporalType(typeof(DateTimeOffset)).ShouldBeTrue();
        CellValues.IsTemporalType(typeof(DateOnly)).ShouldBeTrue();
        CellValues.IsTemporalType(typeof(TimeOnly)).ShouldBeTrue();
        CellValues.IsTemporalType(typeof(DateTime?)).ShouldBeTrue();
        CellValues.IsTemporalType(typeof(TimeSpan)).ShouldBeFalse();
        CellValues.IsTemporalType(typeof(string)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("null")]
    [InlineData(" NULL ")]
    public void both_sides_read_the_reserved_tokens_the_same_way(string text)
        => CellExpressions.IsToken(text, CellTokens.Null).ShouldBeTrue();

    [Theory]
    [InlineData("\"NULL\"", true)]
    [InlineData("\"\"", true)]
    [InlineData("\"", false)]
    [InlineData("NULL", false)]
    public void both_sides_recognise_a_quoted_literal(string text, bool quoted)
        => CellExpressions.IsQuoted(text).ShouldBe(quoted);
}
