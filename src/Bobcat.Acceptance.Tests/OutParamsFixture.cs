using Bobcat;

namespace Bobcat.Acceptance.Tests;

public class OutParamsFixture : Fixture
{
    [When("dividing {int} by {int} gives {int} remainder {int}")]
    public void DivMod(int dividend, int divisor, out int quotient, out int remainder)
    {
        quotient = dividend / divisor;
        remainder = dividend % divisor;
    }
}
