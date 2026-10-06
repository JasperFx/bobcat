namespace Bobcat.Engine;

/// <summary>
/// What a cell actually compared — the closed set of comparisons Bobcat can render (issue #384).
/// </summary>
/// <remarks>
/// <para>
/// <b>Being closed IS the whitelist.</b> A producer cannot describe a comparison Bobcat has no
/// member for, so the honest degradation — a step line with no cell at all — is the only thing the
/// type system permits. A free string would let a producer invent a comparison the renderer cannot
/// render and nothing can check, which is the failure mode this exists to prevent rather than a
/// limitation of it.
/// </para>
/// <para>
/// <b>Why a cell has to say this at all.</b> <see cref="CellResult"/> was equality-shaped by
/// construction: <c>Expected</c> + <c>Actual</c>, rendered as <c>expected 'x', got 'y'</c>. That
/// sentence is a *claim about equality*, so a cell produced by a non-equality assertion stated
/// something false — <c>calculator.Value.ShouldBeGreaterThan(10)</c> against 3 rendered
/// <c>expected '10', got '3'</c>, and 10 was the bound rather than the expectation. The sentence
/// above the cell happened to read correctly, because the Shouldly dialect writes the comparison
/// into the step text; a consumer rendering cells as a grid saw only the false half.
/// </para>
/// <para>
/// <b>It is rendered in one place</b>, <see cref="CellResult.DisplayText"/>, which is what every
/// surface already reads — the Spectre grid, the inline sentence, the JSON report and the monitor
/// wire. So they cannot disagree about what a comparison said, the same discipline
/// <c>CellValues</c> and <c>ColumnNames</c> follow.
/// </para>
/// </remarks>
public enum Comparison
{
    /// <summary>The default shape, and what every table and set comparison makes.</summary>
    Equals,

    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Contains,
    StartsWith,
    EndsWith,

    /// <summary>
    /// Equality within a tolerance. It cannot share a row shape with <see cref="Equals"/>: rendering
    /// <c>ShouldBe(x, 0.01)</c> as though it were exact is a lie, and the tolerance travels in the
    /// cell's note (<c>±0.01</c>) where the existing checkers already put it.
    /// </summary>
    Approximately,

    IsNull,
    IsNotNull,
    IsEmpty,
    IsNotEmpty
}

/// <summary>How a <see cref="Comparison"/> reads in a cell, and whether it has an expected value.</summary>
public static class Comparisons
{
    /// <summary>
    /// Whether the comparison compares against a value. The unary ones do not, so a cell for one
    /// renders what the value WAS and nothing it should have equalled.
    /// </summary>
    public static bool HasExpectedValue(this Comparison comparison)
        => comparison is not (Comparison.IsNull or Comparison.IsNotNull
            or Comparison.IsEmpty or Comparison.IsNotEmpty);

    /// <summary>
    /// The comparison as a reader sees it. <see cref="Comparison.Equals"/> keeps the word
    /// "expected", because that is what every table and set cell has always said and it is correct
    /// for equality; everything else says what it actually checked.
    /// </summary>
    public static string Prose(this Comparison comparison) => comparison switch
    {
        Comparison.Equals => "expected",
        Comparison.NotEquals => "should not be",
        Comparison.GreaterThan => "should be greater than",
        Comparison.GreaterThanOrEqual => "should be at least",
        Comparison.LessThan => "should be less than",
        Comparison.LessThanOrEqual => "should be at most",
        Comparison.Contains => "should contain",
        Comparison.StartsWith => "should start with",
        Comparison.EndsWith => "should end with",
        Comparison.Approximately => "should be approximately",
        Comparison.IsNull => "should be null",
        Comparison.IsNotNull => "should not be null",
        Comparison.IsEmpty => "should be empty",
        Comparison.IsNotEmpty => "should not be empty",
        _ => "expected"
    };

    /// <summary>The wire's word for it — camelCase of the member name, or null for unstated.</summary>
    public static string? OnTheWire(this Comparison? comparison)
        => comparison is null
            ? null
            : char.ToLowerInvariant(comparison.Value.ToString()[0]) + comparison.Value.ToString().Substring(1);
}
