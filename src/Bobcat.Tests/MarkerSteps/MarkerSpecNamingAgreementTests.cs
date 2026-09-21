using System.Reflection;
using Shouldly;
using GeneratorNaming = Bobcat.Generators.MarkerSpecNaming;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// Pins the generator's copy of the marker lane's naming rules
/// (<c>Bobcat.Generators.MarkerSpecNaming</c>, linked in from Bobcat.Generators) to the runtime's
/// (<see cref="MarkerStepRun"/> over <see cref="MarkerSpecNaming"/>) — issue #110.
/// </summary>
/// <remarks>
/// <para>
/// The generator stamps a projected test's <c>{Feature}/{Scenario}</c> identity at compile time —
/// it has to, because marker comments are erased and nothing at runtime can see them — and the
/// runtime publishes that same string on <c>scenario_finished</c>. If the two derivations drift,
/// the suite loses BOTH halves of what the lane is for, and reports neither: the declared steps
/// are looked up by identity (<c>DeclaredSteps.For(Uid)</c>) so the scenario renders as no steps
/// at all, and the run evidence joins no design-time slice.
/// </para>
/// <para>
/// They HAD drifted. <c>CodeFirstNamingAgreementTests</c> pinned this same generator source, but
/// against the code-first lane's <c>SpecificationFeature</c> — never against the marker lane's
/// runtime — and went out with that lane. Measured across the gap, the generator stripped a
/// <c>Specs</c>/<c>Fixture</c> suffix and Pascal-split where the runtime only swapped underscores
/// (<c>WalletSpecs</c> → "Wallet" vs "WalletSpecs"), and the runtime swapped underscores where
/// the generator did not (<c>rebuilding_projections</c> → "rebuilding projections" vs
/// "rebuilding_projections" — the shape of the suite this lane was built for). The generator's
/// derivation won; see the commit. This test is the thing that would have said so.
/// </para>
/// <para>
/// Same guard as <c>SliceTagParsingAgreementTests</c> and <c>SpecOwnershipParsingAgreementTests</c>,
/// and duplicated for the same reason: Bobcat.Generators is netstandard2.0 and references nothing.
/// </para>
/// </remarks>
public class MarkerSpecNamingAgreementTests
{
    /// <summary>
    /// Class names, exercised as the generator sees them (a string off the syntax tree) and as the
    /// runtime sees them (a <see cref="Type"/>) side by side. Every shape two hand-written
    /// derivations drift on: each strippable suffix, a suffix that is only a prefix of one, the
    /// snake_case an adopted xUnit suite is actually written in, PascalCase, both together, an
    /// acronym, and a name that is nothing but a suffix.
    /// </summary>
    public static TheoryData<string> ClassNames() =>
    [
        "Ordering",
        "WalletSpecs",
        "WalletSpec",
        "WalletSpecification",
        "BookingFixture",
        "Specs",
        "Specification",
        "Specifications",
        "ProjectionRebuildTests",
        "rebuilding_projections",
        "event_projection_scenarios",
        "daemon_with_multi_tenancy",
        "Wallet_Audit",
        "Wallet_AuditSpecs",
        "_leading_underscore",
        "trailing_underscore_",
        "double__underscore",
        "HTTPResponse",
        "HTTPResponseSpecs",
        "HTTP",
        "lowercase",
        "X",
    ];

    [Theory]
    [MemberData(nameof(ClassNames))]
    public void feature_titles_agree_without_an_attribute(string className)
        => GeneratorNaming.FeatureTitle(className, null)
            .ShouldBe(MarkerSpecNaming.FeatureTitle(className, null));

    /// <summary>
    /// Method names. The runtime is reached through <see cref="MarkerStepRun"/> rather than
    /// through <c>MarkerSpecNaming</c> directly, because <c>MarkerStepRun</c> is what actually
    /// mints the published identity and is the thing that must not drift.
    /// </summary>
    public static TheoryData<string> MethodNames() =>
    [
        "runs",
        "a_wallet_is_credited",
        "the_daemon_catches_up",
        "Accepting_an_assignment_twice",
        "EventsThenResponse",
        "HTTPResponseIsReturned",
        // What the scaffolder emits for a curated scenario named "2 proposals". Read with a plain
        // Replace this came back as " 2 proposals" — a leading space, and an identity that joins
        // nothing.
        "_2_proposals",
        "trailing_",
        "double__underscore",
        "X",
    ];

    [Theory]
    [MemberData(nameof(MethodNames))]
    public void scenario_titles_agree(string methodName)
        => GeneratorNaming.ScenarioTitle(methodName)
            .ShouldBe(MarkerStepRun.ScenarioNameFor(methodName));

    /// <summary>
    /// An explicit <c>[BobcatFeature("...")]</c> is honoured verbatim by both, including the empty
    /// and whitespace spellings — the generator takes the attribute's argument off the syntax tree
    /// and the runtime takes it off the attribute instance, and "verbatim" has to mean the same
    /// thing on both sides or a titled suite drifts too.
    /// </summary>
    [Theory]
    [InlineData("Async daemon")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Slashes/and spaces")]
    public void an_explicit_feature_title_is_honoured_verbatim_by_both(string title)
    {
        GeneratorNaming.FeatureTitle("IgnoredClassName", title).ShouldBe(title);
        MarkerSpecNaming.FeatureTitle("IgnoredClassName", title).ShouldBe(title);
    }

    /// <summary>
    /// The whole identity, end to end: the generator's registration key and the runtime's
    /// published <c>Uid</c> for the same class and method. This is the string the join is made of,
    /// so agreeing on the halves is worth nothing if the whole ever differs.
    /// </summary>
    [Theory]
    [InlineData(typeof(WalletSpecs), nameof(WalletSpecs.a_wallet_is_credited))]
    [InlineData(typeof(WalletSpecs), nameof(WalletSpecs.EventsThenResponse))]
    [InlineData(typeof(rebuilding_projections), nameof(rebuilding_projections.the_daemon_catches_up))]
    [InlineData(typeof(TitledByAttribute), nameof(TitledByAttribute.runs))]
    public void the_whole_identity_agrees(Type declaringType, string methodName)
    {
        var attributeTitle = declaringType
            .GetCustomAttribute<BobcatFeatureAttribute>()?.Title;

        var generated =
            GeneratorNaming.FeatureTitle(declaringType.Name, attributeTitle)
            + "/" + GeneratorNaming.ScenarioTitle(methodName);

        var method = declaringType.GetMethod(methodName)!;
        var published =
            MarkerStepRun.FeatureNameFor(method) + "/" + MarkerStepRun.ScenarioNameFor(method);

        published.ShouldBe(generated);
    }

    /// <summary>
    /// The decided derivation, pinned as values rather than only as agreement — two
    /// implementations can agree on the wrong answer, and the acronym case below was the wrong
    /// answer on both sides until this wave ("HTTP sponse", the matched characters deleted by a
    /// <c>" $1"</c> replacement over a two-group alternation).
    /// </summary>
    [Theory]
    [InlineData("WalletSpecs", "Wallet")]
    [InlineData("BookingFixture", "Booking")]
    [InlineData("Ordering", "Ordering")]
    [InlineData("ProjectionRebuildTests", "Projection Rebuild Tests")]
    [InlineData("rebuilding_projections", "rebuilding projections")]
    [InlineData("HTTPResponse", "HTTP Response")]
    public void the_feature_derivation_is_the_decided_one(string className, string expected)
        => MarkerSpecNaming.FeatureTitle(className, null).ShouldBe(expected);

    [Theory]
    [InlineData("a_wallet_is_credited", "a wallet is credited")]
    [InlineData("EventsThenResponse", "Events Then Response")]
    [InlineData("_2_proposals", "2 proposals")]
    public void the_scenario_derivation_is_the_decided_one(string methodName, string expected)
        => MarkerStepRun.ScenarioNameFor(methodName).ShouldBe(expected);

    public class WalletSpecs
    {
        public void a_wallet_is_credited() { }
        public void EventsThenResponse() { }
    }

    public class rebuilding_projections
    {
        public void the_daemon_catches_up() { }
    }

    [BobcatFeature("Async daemon")]
    public class TitledByAttribute
    {
        public void runs() { }
    }
}
