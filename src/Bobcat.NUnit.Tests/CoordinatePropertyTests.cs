using System.Runtime.CompilerServices;
using NUnit.Framework;
using NUnit.Framework.Internal;
using Shouldly;

namespace Bobcat.NUnit.Tests;

/// <summary>
/// bobcat#573: a spec's Event Model coordinates are NUnit properties on it, from the binding the
/// generator registers. This project runs no generator, so the binding is made the way the generated
/// module initializer makes it. Each test reads the properties off the running test and its fixture,
/// which is NUnit's answer, not the attribute's.
/// </summary>
/// <remarks>
/// On the fixture NUnit has built no tests yet, so a class-level <c>[BobcatScenario]</c> puts on the
/// fixture what every bound test in the class agrees on — here only the domain — and NUnit's filters
/// match a test through its fixture. On a method, each test gets its own.
/// </remarks>
[BobcatFeature("Coordinates"), BobcatScenario]
public class CoordinatePropertyTests
{
    [ModuleInitializer]
    internal static void Bind()
    {
        var type = typeof(CoordinatePropertyTests).FullName!;
        DeclaredSteps.Bind("Coordinates/a deposit", type, nameof(a_deposit_has_its_own_slice), null,
            "Deposit funds", "Money in", "Accounts", "Command");
        DeclaredSteps.Bind("Coordinates/a summary", type, nameof(a_summary_on_the_same_fixture_has_another), null,
            "AccountSummary", null, "Accounts", "View");

        DeclaredSteps.Bind("Method Level Coordinates/a marked method",
            typeof(MethodLevelCoordinateTests).FullName!, nameof(MethodLevelCoordinateTests.a_marked_method_has_its_slice), null,
            "Deposit funds", null, null, null);
    }

    private static List<object> fixtureProperty(string name)
        => TestExecutionContext.CurrentContext.CurrentTest.Parent!.Properties[name].Cast<object>().ToList();

    [Test]
    public void the_fixture_carries_what_its_tests_agree_on()
    {
        fixtureProperty("Domain").ShouldBe(["Accounts"]);

        // The two bound tests are on different slices, patterns and chapters, so none of those is claimed
        fixtureProperty("Slice").ShouldBeEmpty();
        fixtureProperty("Pattern").ShouldBeEmpty();
        fixtureProperty("Chapter").ShouldBeEmpty();
    }

    // Bound above on different slices, which is what the fixture test reads; nothing to assert in the body
    [Test]
    public void a_deposit_has_its_own_slice()
    {
    }

    [Test]
    public void a_summary_on_the_same_fixture_has_another()
    {
    }
}

public class MethodLevelCoordinateTests
{
    [Test, BobcatScenario]
    public void a_marked_method_has_its_slice()
        => TestContext.CurrentContext.Test.Properties["Slice"].Cast<object>().ShouldBe(["Deposit funds"]);

    [Test]
    public void an_unmarked_method_beside_it_gains_nothing()
        => TestContext.CurrentContext.Test.Properties["Slice"].Cast<object>().ShouldBeEmpty();
}
