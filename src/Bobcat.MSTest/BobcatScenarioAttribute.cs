namespace Bobcat.MSTest;

/// <summary>
/// Opens a Bobcat scenario around an MSTest test, so its marker comments and <c>[BobcatStep]</c>
/// helpers render as specification steps and its verdict reaches a running Bobcat console
/// (issue #386).
/// </summary>
/// <remarks>
/// <para>
/// Put it on a test method, or on the class to cover every test in it:
/// </para>
/// <code>
/// [TestClass, BobcatFeature("Booking appointments"), BobcatScenario]
/// public class BookingSpecs
/// {
///     [TestMethod]
///     public async Task a_proposed_appointment_is_confirmed() { /* Given ... */ }
/// }
/// </code>
/// <para>
/// <b>Unlike the other adapters this attribute does nothing itself.</b> MSTest has no per-test hook
/// an attribute can implement, so the bracket runs from an assembly-wide
/// <c>[GlobalTestInitialize]</c>/<c>[GlobalTestCleanup]</c> pair that this package compiles into the
/// test project (see <see cref="MSTestScenarioBracket"/>), and this attribute is how a test is
/// chosen. That keeps adoption to one attribute on one class at a time — no base class, no
/// replacement for <c>[TestClass]</c> or <c>[TestMethod]</c>, and no change to a signature.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class BobcatScenarioAttribute : Attribute, IDeclaresLifecycle
{
    /// <summary>
    /// Where the specifications this attribute covers stand (bobcat#545) — on a class, every test in
    /// it; on a method, that test, beating its class's.
    /// </summary>
    /// <remarks>
    /// An <see cref="Bobcat.Lifecycle.Acceptance"/> spec runs every time and is recorded in full when it
    /// fails. On MSTest only a spec failed by gathered wrongs can be reported inconclusive; one whose body
    /// threw stays failed (see <see cref="MSTestScenarioBracket.Close"/>). <c>BOBCAT_LIFECYCLE=strict</c>
    /// makes every failure real.
    /// </remarks>
    public Lifecycle Lifecycle
    {
        get => _lifecycle ?? Lifecycle.Regression;
        set => _lifecycle = value;
    }

    private Lifecycle? _lifecycle;

    Lifecycle? IDeclaresLifecycle.Lifecycle => _lifecycle;
}
