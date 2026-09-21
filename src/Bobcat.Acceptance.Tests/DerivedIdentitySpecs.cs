using System.Reflection;
using Bobcat;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Issue #110, the half of the end-to-end path that <see cref="MarkerCommentStepsTests"/> cannot
/// reach: a projected suite whose <c>{Feature}/{Scenario}</c> identity is DERIVED rather than
/// written down.
/// </summary>
/// <remarks>
/// <para>
/// Every other marker-lane class in this repo carries an explicit
/// <c>[BobcatFeature("some title")]</c>, and a literal title is a string both sides copy — there
/// is nothing in it for two derivations to disagree about. That is exactly why the generator and
/// the runtime were free to drift on the derived spelling for as long as they did: the class
/// name here (<c>…Specs</c>) and the method name (PascalCase) are the two shapes they disagreed
/// on, and no test in the suite went anywhere near them.
/// </para>
/// <para>
/// The generator runs over this assembly, so this is the real loop and not a model of it: the
/// comments below reach runtime only if the identity the generator registered them under is the
/// identity <see cref="MarkerStepRun"/> asks for. Before the derivations were reconciled the
/// generator filed these steps under "Derived Identity/The Steps Survive A Derived Identity" and
/// the runtime looked them up under "DerivedIdentitySpecs/TheStepsSurviveADerivedIdentity",
/// and the only symptom was a scenario that rendered no steps at all.
/// </para>
/// </remarks>
[BobcatFeature]
public class DerivedIdentitySpecs
{
    [Fact]
    public void TheStepsSurviveADerivedIdentity()
    {
        // Given a class whose feature title is derived, not written down
        var method = MethodBase.GetCurrentMethod() as MethodInfo;

        // When the identity is built the way the runtime builds it
        var uid = MarkerStepRun.FeatureNameFor(method!) + "/" + MarkerStepRun.ScenarioNameFor(method!);

        // Then it is the identity the generator filed the steps under
        uid.ShouldBe("Derived Identity/The Steps Survive A Derived Identity");

        DeclaredSteps.For(uid).Select(x => x.ToString()).ShouldBe(
        [
            "Given a class whose feature title is derived, not written down",
            "When the identity is built the way the runtime builds it",
            "Then it is the identity the generator filed the steps under"
        ]);
    }
}
