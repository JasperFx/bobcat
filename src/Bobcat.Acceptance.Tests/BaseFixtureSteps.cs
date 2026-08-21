using Bobcat;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// A base fixture that declares steps and a lifecycle hook, to prove the generator discovers
/// inherited <c>[Given]/[When]/[Then]/[Check]</c> methods from base classes (issue #104) — not only
/// the ones declared on the most-derived fixture. This is the "a fixture IS a … fixture" route the
/// shipped Critter Stack grammar rides on.
/// </summary>
public abstract class CalculatorBase : Fixture
{
    protected int Total;

    // Base-declared lifecycle hook — runs before the derived one, base-first.
    public void BeforeEach() => Order.Add("base.BeforeEach");

    public static readonly List<string> Order = new();

    [Given("the running total starts at {int}")]
    public void Start(int n) => Total = n;

    [When("I add {int}")]
    public void Add(int n) => Total += n;

    [Then("the running total is {int}")]
    public int RunningTotal() => Total;

    // A step the derived class will override with the same text — the derived one must win.
    [Then("the label is {string}")]
    [Check("the label is {string}")]
    public virtual bool Label(string expected) => expected == "base";
}

/// <summary>
/// Derives from <see cref="CalculatorBase"/>, adds one of its own steps, and hides the base's
/// <c>the label is …</c> — most-derived wins (BOBCAT015 is an info diagnostic, not an error).
/// </summary>
[FixtureTitle("Derived Calculator")]
public class DerivedCalculatorFixture : CalculatorBase
{
    public void AfterEach() => Order.Add("derived.AfterEach");

    [When("I subtract {int}")]
    public void Subtract(int n) => Total -= n;

    [Then("the label is {string}")]
    [Check("the label is {string}")]
    public override bool Label(string expected) => expected == "derived";
}
