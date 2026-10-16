using System.Collections;
using System.Globalization;

namespace Bobcat;

/// <summary>
/// How one row of a data-driven specification — an xUnit <c>[Theory]</c> row, a TUnit
/// <c>[Arguments]</c>, an NUnit <c>[TestCase]</c>, an MSTest <c>[DataRow]</c> — gets an identity of
/// its own (bobcat#569).
/// </summary>
/// <remarks>
/// <para>
/// <b>Row identity = method identity + the row.</b> The row is the test's named arguments,
/// <c>Discounts/Discount is applied (amount: 100, tier: Gold)</c>, or its label when it has one,
/// <c>Discounts/Discount is applied [gold customer]</c>. Before this every row recorded under the
/// method's <c>{Feature}/{Scenario}</c>, so the ledger mixed the rows' durations and flakiness and a
/// console received N verdicts under one id.
/// </para>
/// <para>
/// <b>Values are formatted with the invariant culture</b> so the string is the same on every
/// machine and every run — it is a key the ledger, the manifest and a console join on.
/// </para>
/// <para>
/// <b>The generator holds a copy</b> (<c>Bobcat.Generators.MarkerSpecNaming.RowTitle</c> and its
/// constant formatter), because the constant rows are listed in the spec manifest at compile time.
/// <c>MarkerSpecNamingAgreementTests</c> pins the two together.
/// </para>
/// </remarks>
public static class SpecRows
{
    /// <summary>
    /// The row part of a data-driven test's identity: <c>[label]</c> when the row has a label,
    /// <c>(name: value, …)</c> when it has arguments, or null when the test is not data-driven.
    /// </summary>
    /// <param name="parameterNames">The test method's parameter names, in order.</param>
    /// <param name="arguments">The values this row passes, in order. Null or empty for a test with no row.</param>
    /// <param name="label">The row's label (<c>[BobcatExample("…")]</c>, xUnit's <c>Label</c>), if any.</param>
    public static string? Describe(IReadOnlyList<string> parameterNames, IReadOnlyList<object?>? arguments, string? label)
    {
        if (label is { } text && text.Trim().Length > 0) return Labelled(text);
        if (arguments is null || arguments.Count == 0) return null;

        return Arguments(parameterNames, arguments.Select(Format).ToList());
    }

    /// <summary>The row part for a labelled row: <c>[label]</c>.</summary>
    public static string Labelled(string label) => "[" + escape(label.Trim()) + "]";

    /// <summary>
    /// The row part for already formatted values: <c>(name: value, …)</c>. A value with no parameter
    /// to name it (a runner handed over more than the signature declares) is named by its position.
    /// </summary>
    public static string Arguments(IReadOnlyList<string> parameterNames, IReadOnlyList<string> formattedValues)
        => "(" + string.Join(", ", formattedValues.Select((value, i) =>
            (i < parameterNames.Count ? parameterNames[i] : "arg" + i) + ": " + value)) + ")";

    /// <summary>The scenario title for a row: the method's title, a space, then the row part.</summary>
    public static string Title(string scenarioTitle, string? row)
        => row is null ? scenarioTitle : scenarioTitle + " " + row;

    /// <summary>
    /// One argument value as it appears in a row identity. Strings verbatim, <c>null</c>,
    /// <c>true</c>/<c>false</c>, an enum by member name, a type by name, numbers invariant-culture
    /// (floating point round-trippable), a collection as <c>[a, b]</c>. Line breaks are escaped so an
    /// identity is always one line.
    /// </summary>
    public static string Format(object? value) => value switch
    {
        null => "null",
        string text => escape(text),
        char c => escape(c.ToString()),
        bool b => b ? "true" : "false",
        Enum e => e.ToString(),
        Type type => type.Name,
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => escape(formattable.ToString(null, CultureInfo.InvariantCulture)),
        IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Format)) + "]",
        _ => escape(value.ToString() ?? "null")
    };

    private static string escape(string text)
        => text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
