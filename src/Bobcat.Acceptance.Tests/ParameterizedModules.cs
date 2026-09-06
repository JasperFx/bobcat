using Bobcat;
using Bobcat.Engine;

namespace Bobcat.Acceptance.Tests;

// --- Issue #212 phase 2: parameterized grammar modules. The vocabulary stays a compile-time
//     fact (the generator reads these steps from the type symbols); the BINDING is a
//     construction fact — [IncludeGrammars] literals plus scenario-scope resolutions flow to
//     the module's constructor, per scenario.

/// <summary>
/// A module bound by attribute literals. The trailing optional parameter is deliberately not
/// covered by the attributes below, proving omitted-optional construction.
/// </summary>
public class PrefixedEchoModule
{
    private readonly string _prefix;
    private readonly string _suffix;

    public PrefixedEchoModule(string prefix, string suffix = "!")
    {
        _prefix = prefix;
        _suffix = suffix;
    }

    [Then("the prefixed echo of {string} should be {string}")]
    public string Echo(string value) => _prefix + value + _suffix;
}

/// <summary>The capture <see cref="ContextBoundModule"/> publishes.</summary>
public sealed record BoundLabel(string Value);

/// <summary>
/// A module whose constructor mixes an attribute literal with a scenario-scope resolution
/// (IStepContext), forcing the lazy per-scenario construction path — the scope only exists
/// inside a step, so the first step using the module constructs it there.
/// </summary>
public class ContextBoundModule
{
    private readonly string _label;
    private readonly IStepContext _context;

    public ContextBoundModule(string label, IStepContext context)
    {
        _label = label;
        _context = context;
    }

    [When("the bound module records its label")]
    public void Record() => _context.SetState(new BoundLabel(_label));

    [Then("the bound label should be {string}")]
    public string Label() => _context.GetState<BoundLabel>().Value;
}

/// <summary>A module only the abstract base fixture below declares.</summary>
public class FlagModule
{
    private readonly string _flag;

    public FlagModule(string flag = "off") => _flag = flag;

    [Then("the inherited flag should be {string}")]
    public string Flag() => _flag;
}

[FixtureTitle("Parameterized Composed")]
[IncludeGrammars(typeof(PrefixedEchoModule), "pre-")]
[IncludeGrammars(typeof(ContextBoundModule), "wallet")]
public class ParameterizedComposedFixture : Fixture;

/// <summary>
/// A shipped-assembly-shaped base: it carries its modules on the class, so a derived fixture
/// composes them with no attribute of its own — the CritterStackHttpFixture pattern.
/// </summary>
[IncludeGrammars(typeof(FlagModule), "on")]
[IncludeGrammars(typeof(PrefixedEchoModule), "base-")]
public abstract class BaseComposedFixture : Fixture;

/// <summary>
/// Inherits <see cref="FlagModule"/> from the base untouched and re-parameterizes
/// <see cref="PrefixedEchoModule"/> — the most-derived declaration wins, the way a derived
/// step hides a base one.
/// </summary>
[FixtureTitle("Derived Composed")]
[IncludeGrammars(typeof(PrefixedEchoModule), "derived-")]
public class DerivedComposedFixture : BaseComposedFixture;
