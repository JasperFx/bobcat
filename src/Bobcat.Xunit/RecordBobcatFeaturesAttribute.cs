using System.Reflection;
using Xunit.v3;

namespace Bobcat.Xunit;

/// <summary>
/// Makes <c>[BobcatFeature]</c> on its own enough to record an xUnit test class: every test in a
/// feature class opens a scenario, with no <see cref="BobcatScenarioAttribute"/> or
/// <see cref="BobcatSpecAttribute"/> beside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an assembly attribute.</b> <c>[BobcatFeature]</c> lives in core Bobcat, which cannot
/// depend on any runner, so it cannot be an xUnit before/after hook itself — that is the only reason
/// a projected class used to need a second attribute. xUnit v3 also collects
/// <see cref="IBeforeAfterTestAttribute"/> from the ASSEMBLY, so this one hook, applied once, looks
/// at each test's class and opens the bracket when that class is a feature.
/// </para>
/// <para>
/// <b>No code in a consuming suite.</b> The package's <c>buildTransitive</c> props add it as an
/// MSBuild <c>AssemblyAttribute</c>, the same way the test-platform hook is registered. An in-repo
/// <c>ProjectReference</c> does not consume those build assets and writes
/// <c>[assembly: Bobcat.Xunit.RecordBobcatFeatures]</c> itself.
/// </para>
/// <para>
/// <b>Never a second bracket.</b> A class or test that already carries an attribute opening the
/// scenario — <c>[BobcatScenario]</c>, <c>[BobcatSpec]</c>, or a subclass of either — keeps it, and
/// this hook stands aside, so an existing suite behaves exactly as before.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class RecordBobcatFeaturesAttribute : Attribute, IBeforeAfterTestAttribute
{
    // Per async context, because the attribute instance is shared by every test in the assembly.
    private static readonly AsyncLocal<bool> _opened = new();

    public void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        _opened.Value = false;
        if (!Applies(test.TestMethod.TestClass.Class, methodUnderTest)) return;

        XunitScenarioBracket.Open(methodUnderTest);
        _opened.Value = true;
    }

    public void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (!_opened.Value) return;
        _opened.Value = false;

        XunitScenarioBracket.Close();
    }

    /// <summary>
    /// Does this hook open the scenario for a test: its class is a <c>[BobcatFeature]</c> and
    /// nothing on the class or the method opens one already.
    /// </summary>
    public static bool Applies(Type testClass, MethodInfo method)
        => testClass.IsDefined(typeof(BobcatFeatureAttribute), inherit: true)
           && !opensItself(testClass.GetCustomAttributes(inherit: true))
           && !opensItself(method.GetCustomAttributes(inherit: true));

    private static bool opensItself(object[] attributes)
        => attributes.Any(x => x is BobcatScenarioAttribute or BobcatSpecAttribute);
}
