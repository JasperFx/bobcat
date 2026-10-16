using System.Reflection;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Bobcat.Xunit;

/// <summary>
/// One labelled row of a data-driven specification (bobcat#569): <c>[InlineData]</c> with a name.
/// The row's identity is <c>{Feature}/{Scenario} [label]</c> instead of the argument form
/// <c>{Feature}/{Scenario} (amount: 100, tier: Gold)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reach for it when the arguments do not say what the row is about.</b> A label is the stable
/// half of the identity: change an argument and an argument-form identity changes with it (and so
/// moves the row's ledger history), while a labelled row keeps its name.
/// </para>
/// <para>
/// It is xUnit's own <see cref="DataAttribute.Label"/> with the label first, so a runner shows the
/// same name Bobcat records. <c>[InlineData(100, "Gold", Label = "gold customer")]</c> is the same
/// row. Two rows of one method with the same label share an identity, which the generator refuses as
/// BOBCAT036.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class BobcatExampleAttribute : DataAttribute
{
    /// <param name="label">What this row is an example of. Becomes <c>[label]</c> in its identity.</param>
    /// <param name="data">The values passed to the test method, as <c>[InlineData]</c> takes them.</param>
    public BobcatExampleAttribute(string label, params object?[]? data)
    {
        Label = label;

        // params with a single null argument binds the array itself to null, the same corner
        // InlineData handles: it means one null value.
        Data = data ?? [null];
    }

    /// <summary>The values passed to the test method.</summary>
    public object?[] Data { get; }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyCollection<ITheoryDataRow>> GetData(
        MethodInfo testMethod, DisposalTracker disposalTracker)
        => new([ConvertDataRow(Data)]);

    /// <inheritdoc />
    public override bool SupportsDiscoveryEnumeration() => true;
}
