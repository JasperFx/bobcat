using Bobcat;
using Bobcat.Xunit;
using Shouldly;
using Xunit;

namespace Bobcat.Console.SampleWeb.Specs;

[BobcatFeature("Greeting"), BobcatScenario]
public class Greeting
{
    [Fact]
    public void greets_by_name()
    {
        // Then the greeting names the visitor
        Greetings.For("Ada").ShouldBe("Hello, Ada");
    }
}
