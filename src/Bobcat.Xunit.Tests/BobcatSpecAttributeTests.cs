using System.Reflection;
using System.Runtime.CompilerServices;
using Shouldly;
using Xunit;
using Xunit.v3;

namespace Bobcat.Xunit.Tests;

/// <summary>
/// Issue #403: <c>[BobcatSpec]</c> is the <c>[Fact]</c>, the <c>[BobcatScenario]</c> and the
/// <c>[BobcatSlice]</c> at once. These are the two facts about xunit.v3 3.2.2 that make that
/// possible, pinned against the real runner rather than reasoned about.
/// </summary>
[BobcatFeature("Projected specifications")]
public class BobcatSpecAttributeTests
{
    /// <summary>
    /// xUnit discovers it as a test at all — the first half of "one attribute does three jobs".
    /// If <see cref="FactAttribute"/> were sealed, or discovery matched the exact type, this
    /// method would simply never run, which is why it asserts something only a running body can.
    /// </summary>
    [BobcatSpec]
    public void the_attribute_is_discovered_as_a_test()
        => ScenarioRecorder.Current.ShouldNotBeNull(
            "a running body proves discovery, and an open recording proves the implied scenario");

    /// <summary>
    /// <b>It implies <c>[BobcatScenario]</c>, with nothing on the class.</b> This class carries no
    /// <c>[BobcatScenario]</c> — only <c>[BobcatFeature]</c> for the title — so the open recording
    /// and its identity can only have come from the attribute on the method.
    /// </summary>
    /// <remarks>
    /// The mechanism: xUnit v3 collects test-bracket hooks by the
    /// <see cref="IBeforeAfterTestAttribute"/> INTERFACE, not only from the
    /// <c>BeforeAfterTestAttribute</c> base class. Those two are siblings — both derive straight
    /// from <see cref="Attribute"/> — so a single attribute could never have inherited from both,
    /// and implementing the interface on a <see cref="FactAttribute"/> subclass is the only way
    /// the pair collapses. It is undocumented enough to be worth a test rather than a comment.
    /// </remarks>
    [BobcatSpec]
    public void it_opens_the_scenario_without_a_class_level_attribute()
        => ScenarioRecorder.Current!.Uid
            .ShouldBe("Projected specifications/it opens the scenario without a class level attribute");

    /// <summary>
    /// <b>The caller parameters are re-declared and forwarded, and this is the test that would
    /// have caught not doing it.</b> <see cref="FactAttribute"/>'s only constructor takes
    /// <c>[CallerFilePath]</c>/<c>[CallerLineNumber]</c>, and the compiler fills them at the call
    /// site it sees. A subclass calling <c>base()</c> without re-declaring them hands over its
    /// OWN file and line, so every test in a suite reports the same source location and IDE test
    /// navigation lands on the attribute instead of the test.
    /// </summary>
    [Fact]
    public void the_caller_file_and_line_point_at_the_test_not_at_the_attribute()
    {
        var spec = factAttributeOn(nameof(it_opens_the_scenario_without_a_class_level_attribute));

        // This file, not BobcatSpecAttribute.cs — the whole failure mode in one assertion.
        Path.GetFileName(spec.SourceFilePath).ShouldBe("BobcatSpecAttributeTests.cs");

        // And the line where [BobcatSpec] is written, which is the line before the method. Read
        // off a plain [Fact] in the same file rather than hard-coded, so the number cannot drift
        // as this file is edited: the claim is "the same answer [Fact] gives", not "line 41".
        var plain = factAttributeOn(nameof(the_caller_file_and_line_point_at_the_test_not_at_the_attribute));
        plain.SourceLineNumber.ShouldNotBeNull();

        spec.SourceLineNumber.ShouldNotBeNull();
        spec.SourceLineNumber.Value.ShouldBeLessThan(plain.SourceLineNumber!.Value);
        lineOf(spec).ShouldContain("[BobcatSpec]");
        lineOf(plain).ShouldContain("[Fact]");
    }

    /// <summary>
    /// The positional form is the ergonomic point of the issue: <c>[BobcatSpec(typeof(X))]</c>
    /// means exactly <c>SliceName = "X"</c>, and the caller parameters still forward past it.
    /// </summary>
    [Fact]
    public void the_positional_constructor_sets_the_slice_type_and_still_forwards_the_caller_info()
    {
        var attribute = new BobcatSpecAttribute(typeof(BobcatSpecAttributeTests));

        attribute.SliceType.ShouldBe(typeof(BobcatSpecAttributeTests));
        Path.GetFileName(attribute.SourceFilePath).ShouldBe("BobcatSpecAttributeTests.cs");
    }

    /// <summary>
    /// Unsealed on purpose — <c>[PostgresFact]</c> in this repository is the shape to expect — and
    /// a subclass inherits the caller-parameter obligation. This is the shape that keeps working.
    /// </summary>
    [Fact]
    public void a_subclass_that_forwards_the_caller_parameters_keeps_pointing_at_its_own_call_site()
    {
        var attribute = factAttributeOn(nameof(a_derived_spec_attribute_still_runs));

        Path.GetFileName(attribute.SourceFilePath).ShouldBe("BobcatSpecAttributeTests.cs");
        lineOf(attribute).ShouldContain("[DerivedSpec]");
    }

    [DerivedSpec]
    public void a_derived_spec_attribute_still_runs()
        => ScenarioRecorder.Current.ShouldNotBeNull();

    private static FactAttribute factAttributeOn(string method)
        => typeof(BobcatSpecAttributeTests).GetMethod(method)!
            .GetCustomAttributes(typeof(FactAttribute), inherit: false)
            .Cast<FactAttribute>()
            .Single();

    private static string lineOf(FactAttribute attribute)
        => File.ReadAllLines(attribute.SourceFilePath!)[attribute.SourceLineNumber!.Value - 1];
}

/// <summary>A consumer's own gated fact attribute, over <c>[BobcatSpec]</c>.</summary>
internal sealed class DerivedSpecAttribute : BobcatSpecAttribute
{
    public DerivedSpecAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
    }
}
