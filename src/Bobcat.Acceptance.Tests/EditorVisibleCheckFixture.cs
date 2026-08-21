using Bobcat;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Every check here is stacked with a <c>[Then]</c> carrying the same expression — the shape
/// docs/editor-integration.md recommends so the VS Code Cucumber extension (which only knows
/// the Given/When/Then short names) can navigate to a check. The two orders exist to pin the
/// rule that <c>[Check]</c> wins regardless of which attribute is written first.
/// </summary>
public class EditorVisibleCheckFixture : Fixture
{
    private int _value;

    [Given("the value is {int}")]
    public void SetValue(int value) => _value = value;

    [Then("the value is positive with then first")]
    [Check("the value is positive with then first")]
    public bool PositiveThenFirst() => _value > 0;

    [Then("the value is negative with then first")]
    [Check("the value is negative with then first")]
    public bool NegativeThenFirst() => _value < 0;

    [Check("the value is positive with check first")]
    [Then("the value is positive with check first")]
    public bool PositiveCheckFirst() => _value > 0;

    [Check("the value is negative with check first")]
    [Then("the value is negative with check first")]
    public bool NegativeCheckFirst() => _value < 0;
}
