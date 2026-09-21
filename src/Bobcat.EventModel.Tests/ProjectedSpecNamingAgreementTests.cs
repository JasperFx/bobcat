using Shouldly;
using GeneratorNaming = Bobcat.Generators.MarkerSpecNaming;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// Pins <see cref="ProjectedSpecNaming"/>'s copy of the marker lane's scenario derivation to the
/// generator's (<c>Bobcat.Generators.MarkerSpecNaming</c>, linked in) — issue #110. Bobcat.Tests
/// pins that same generator source to the runtime, so the three agree transitively.
/// </summary>
/// <remarks>
/// <para>
/// Three copies is the price of three assemblies that reference each other in no direction: the
/// generator is netstandard2.0 and references nothing, and Bobcat.EventModel is a standalone
/// package that takes YamlDotNet and JasperFx.Events and neither of the other two.
/// </para>
/// <para>
/// This one is worth pinning rather than approximating because of what it is FOR.
/// <c>RoundTrips</c> is the read-time warning that a curated scenario name will publish as some
/// other string and join nothing — the failure mode #318 and eleven dead Stoat identities already
/// paid for. A check built on the wrong rule produces both errors it exists to prevent: it stays
/// quiet for a name that really does drift, and it warns about one that does not.
/// </para>
/// </remarks>
public class ProjectedSpecNamingAgreementTests
{
    /// <summary>
    /// Method names, including the ones the two spellings of the rule disagree about: a leading
    /// underscore (what <see cref="ProjectedSpecNaming.MethodNameFor"/> itself emits for a
    /// scenario name starting with a digit), doubled and trailing underscores, and PascalCase.
    /// </summary>
    public static TheoryData<string> MethodNames() =>
    [
        "a_proposal_is_confirmed",
        "the_daemon_catches_up",
        "runs",
        "_2_proposals",
        "trailing_",
        "double__underscore",
        "EventsThenResponse",
        "HTTPResponseIsReturned",
        "X",
    ];

    [Theory]
    [MemberData(nameof(MethodNames))]
    public void the_scenario_derivation_agrees_with_the_generators(string methodName)
        => ProjectedSpecNaming.ScenarioTitleFor(methodName)
            .ShouldBe(GeneratorNaming.ScenarioTitle(methodName));

    /// <summary>
    /// And the round-trip verdict is that derivation applied to the name this type spells, not a
    /// near-miss of it.
    /// </summary>
    [Theory]
    [InlineData("a proposal is confirmed", true)]
    [InlineData("a proposal is confirmed, twice", false)]
    // "2 proposals" spells as _2_proposals and comes back as "2 proposals" — empty segments are
    // dropped, so the leading underscore costs nothing. Read with a plain Replace it came back
    // as " 2 proposals" and this was reported as a name that would not join.
    [InlineData("2 proposals", true)]
    // PascalCase does not survive: it is spelled as itself and read back split.
    [InlineData("AcceptedTwice", false)]
    public void round_trips_uses_that_derivation(string scenarioName, bool expected)
    {
        ProjectedSpecNaming.RoundTrips(scenarioName).ShouldBe(expected);

        ProjectedSpecNaming.RoundTrips(scenarioName).ShouldBe(
            GeneratorNaming.ScenarioTitle(ProjectedSpecNaming.MethodNameFor(scenarioName)) == scenarioName);
    }
}
