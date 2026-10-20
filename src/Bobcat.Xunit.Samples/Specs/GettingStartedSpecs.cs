using Bobcat.Xunit.Samples.Grammars;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The first specification <c>docs/getting-started.md</c> writes, over <see cref="CalculatorSteps"/>.
/// Green, and included into that page whole. This project references Bobcat by project rather than
/// by package, so it states <c>[BobcatScenario]</c> itself; a package consumer on xUnit needs only
/// <c>[BobcatFeature]</c>.
/// </summary>
// #region getting-started-spec
[BobcatFeature("Calculator, getting started"), BobcatScenario]
public class GettingStartedSpecs
{
    private readonly CalculatorSteps _calculator = new();

    [Fact]
    public void using_sentences()
    {
        _calculator.StartWith(3);
        _calculator.MultiplyBy(2);
        _calculator.TheValueShouldBe(6);
        _calculator.AddingNumbersTogether(2, 3, 5);
    }
}
// #endregion getting-started-spec
