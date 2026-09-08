namespace Bobcat;

/// <summary>
/// Marks a test class whose methods project into the Bobcat model (issue #110). The steps come
/// from marker comments in the bodies and from calls to <see cref="BobcatStepAttribute"/> helpers.
/// </summary>
/// <remarks>
/// Deliberately carries no reference to any test framework: the generator matches xUnit, TUnit and
/// NUnit test methods by attribute NAME, because Bobcat cannot depend on a runner it is trying to
/// be neutral about. The runner-specific piece — opening and closing a scenario around each test —
/// lives in the adapter package for that runner.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BobcatFeatureAttribute : Attribute
{
    public BobcatFeatureAttribute(string? title = null) => Title = title;

    /// <summary>The feature title; the class name, prettified, when null.</summary>
    public string? Title { get; }
}

/// <summary>
/// Marks a helper method as a specification step. Calling it renders — and reports — that step.
/// </summary>
/// <remarks>
/// <para>
/// This is the higher-leverage half of the marker-comment style. Marker comments are per-test
/// work; an attribute on a shared helper renders every test that calls it. Marten's
/// <c>DaemonContext</c> is the motivating case: decorating eight helpers renders hundreds of
/// existing tests with no test file touched at all.
/// </para>
/// <para>
/// <b>Text is a template.</b> <c>{threads}</c> binds to the parameter of that name at the call
/// site, so <c>PublishMultiThreaded(3)</c> renders as "the events are published on 3 threads".
/// An unbound placeholder is left as written rather than guessed at.
/// </para>
/// <para>
/// <b>The method must be reachable from a static class in the same assembly</b> — internal or
/// public, never protected. The generated interceptor is an extension method, which is a
/// requirement of the interceptor feature rather than a choice, and an extension method cannot
/// see a protected member.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BobcatStepAttribute : Attribute
{
    public BobcatStepAttribute(string text) => Text = text;

    public string Text { get; }

    /// <summary>Given / When / Then, when the text alone does not say. Optional.</summary>
    public string? Keyword { get; set; }
}
