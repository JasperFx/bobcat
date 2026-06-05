using Bobcat;

namespace Bobcat.Acceptance.Tests;

public class ReturnValueFixture : Fixture
{
    [Then("{int} plus {int} should be {int}")]
    public int Add(int a, int b) => a + b;

    [Approx(0.1)]
    [Then("the average of {int} and {int} is {double}")]
    public double Average(int a, int b) => (a + b) / 2.0;

    [Then("the greeting for {string} should be {string}")]
    public string Greeting(string name) => $"Hello {name}";
}
