using System.Reflection;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Bobcat.Xunit;

/// <summary>
/// A data-driven specification's rows written as one pipe-table literal (bobcat#570): one row, one
/// specification.
/// </summary>
/// <remarks>
/// <code>
/// [BobcatTheory(typeof(ApplyDiscount))]
/// [BobcatExamples("""
///     | amount | tier | expected |
///     | 100    | Gold | 90       |
///     | 100    | None | 100      |
///     """)]
/// public async Task discount_is_applied(decimal amount, Tier tier, decimal expected) { … }
/// </code>
/// <para>
/// Columns bind to the method's parameters by name (a <see cref="HeaderAttribute"/> title, else the
/// name, case-insensitive), and cells read through <c>CellValues</c>, so <c>NULL</c>, <c>EMPTY</c>,
/// relative times, enums by name and registered domain types mean what they mean in any table. See
/// <see cref="SpecExamples"/>.
/// </para>
/// <para>
/// <b>Checked at compile time.</b> The table is a constant, so the generator reads it: a column naming
/// no parameter is BOBCAT032; a missing column, a ragged row or an unreadable cell is BOBCAT042; and
/// each row is listed in the spec manifest under its identity,
/// <c>Discounts/discount is applied (amount: 100, tier: Gold, expected: 90)</c>.
/// </para>
/// <para>
/// <b>Not <c>TableRunner</c>.</b> That judges many rows inside ONE specification; this gives each row
/// its own run, verdict and identity. Rows are read at discovery, so a domain type's
/// <c>CellValues.Register</c> belongs in a module initializer, not in a fixture.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class BobcatExamplesAttribute : DataAttribute
{
    /// <param name="table">
    /// A pipe-table literal: a header row naming the method's parameters, then one row per example.
    /// </param>
    public BobcatExamplesAttribute(string table) => Table = table;

    /// <summary>The table as written.</summary>
    public string Table { get; }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyCollection<ITheoryDataRow>> GetData(
        MethodInfo testMethod, DisposalTracker disposalTracker)
        => new(SpecExamples.Rows(testMethod, Table).Select(ConvertDataRow).ToList());

    /// <inheritdoc />
    public override bool SupportsDiscoveryEnumeration() => true;
}
