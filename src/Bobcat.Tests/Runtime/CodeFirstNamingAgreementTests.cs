using Bobcat.CodeFirst;
using Bobcat.Generators;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Pins the generator's copy of the code-first naming rules (<c>CodeFirstNaming</c>, linked in
/// from Bobcat.Generators) to the runtime's (<see cref="SpecificationFeature"/>) — issue #170.
/// The generator stamps a code-first slice's <c>{Feature}/{Scenario}</c> identity at compile
/// time and the runtime publishes the same string on <c>scenario_finished</c>; if the two
/// derivations drift, run evidence silently stops joining the design-time model and nothing
/// reports it. Same guard as <c>SliceTagParsingAgreementTests</c> and
/// <c>ResourceParsingAgreementTests</c>.
/// </summary>
public class CodeFirstNamingAgreementTests
{
    // Representative shapes, exercised through the real runtime API (a Type / MethodInfo) and
    // the generator's string functions side by side.

    private class WalletAuditSpecification : Specification
    {
        [Scenario]
        public void auditing_a_credited_wallet() { }

        [Scenario]
        public void EventsThenResponse() { }

        [Scenario("A hand-written title")]
        public void whatever_the_method_is_called() { }

        [Scenario("   ")]
        public void a_blank_attribute_title_falls_back() { }
    }

    private class OrderSagaSpecs : Specification
    {
        [Scenario]
        public void runs() { }
    }

    private class Standalone : Specification
    {
        [Scenario]
        public void runs() { }
    }

    [FixtureTitle("Named By Attribute")]
    private class IgnoredClassName : Specification
    {
        [Scenario]
        public void runs() { }
    }

    [Theory]
    [InlineData(typeof(WalletAuditSpecification), null, "Wallet Audit")]
    [InlineData(typeof(OrderSagaSpecs), null, "Order Saga")]
    [InlineData(typeof(Standalone), null, "Standalone")]
    [InlineData(typeof(IgnoredClassName), "Named By Attribute", "Named By Attribute")]
    public void feature_titles_agree(Type specification, string? attributeTitle, string expected)
    {
        SpecificationFeature.DeriveTitle(specification).ShouldBe(expected);
        CodeFirstNaming.FeatureTitle(specification.Name, attributeTitle).ShouldBe(expected);
    }

    [Theory]
    [InlineData(nameof(WalletAuditSpecification.auditing_a_credited_wallet), "auditing a credited wallet")]
    [InlineData(nameof(WalletAuditSpecification.EventsThenResponse), "Events Then Response")]
    [InlineData(nameof(WalletAuditSpecification.whatever_the_method_is_called), "A hand-written title")]
    [InlineData(nameof(WalletAuditSpecification.a_blank_attribute_title_falls_back), "a blank attribute title falls back")]
    public void scenario_titles_agree(string methodName, string expected)
    {
        var method = typeof(WalletAuditSpecification).GetMethod(methodName)!;
        var attributeTitle = ((ScenarioAttribute)method
            .GetCustomAttributes(typeof(ScenarioAttribute), false).Single()).Title;

        SpecificationFeature.DeriveScenarioTitle(method).ShouldBe(expected);
        CodeFirstNaming.ScenarioTitle(methodName, attributeTitle).ShouldBe(expected);
    }
}
