using Bobcat;

namespace Bobcat.Acceptance.Tests;

public class DecisionTableFixture : Fixture
{
    [DecisionTable]
    [Then("the line totals are calculated")]
    public decimal LineTotal(int quantity, decimal price)
    {
        if (quantity < 0) throw new InvalidOperationException("a line cannot have a negative quantity");
        return quantity * price;
    }

    [DecisionTable]
    [Then("the divmod results are")]
    public void DivModTable(int dividend, int divisor, out int quotient, out int remainder)
    {
        quotient = dividend / divisor;
        remainder = dividend % divisor;
    }
}
