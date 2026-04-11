using Bobcat;

namespace ConsolePreview;

public class CalculatorFixture : Fixture
{
    public int Left { get; set; }
    public int Right { get; set; }
    public int Result { get; set; }

    [Given("the left operand is {int}")]
    public void TheLeftOperandIs(int value) => Left = value;

    [Given("the right operand is {int}")]
    public void TheRightOperandIs(int value) => Right = value;

    [When("the operands are added")]
    public void TheOperandsAreAdded() => Result = Left + Right;

    [When("the operands are subtracted")]
    public void TheOperandsAreSubtracted() => Result = Left - Right;

    [Then("the result should be {int}")]
    public void TheResultShouldBe(int expected)
    {
        if (Result != expected)
            throw new Exception($"Expected {expected} but got {Result}");
    }
}
