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
/// Binds a projected test — a class or a single test method — to an Event Modeling slice
/// (issue #324).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A slice whose right test is a plain xUnit or TUnit test used to be
/// invisible on the Event Model: <see cref="BobcatFeatureAttribute"/> renders the test as a
/// readable specification, but carried no slice identity, so the slice stayed unbound however green
/// the test was, and no spec-identity gate could join it.
/// </para>
/// <para>
/// <b>Binding only — never roles.</b> This says WHICH slice the test is evidence for. It does not
/// say what the slice's command, events or read models are: the curated model states those on the
/// Declared rung and the code states them on Derived, and a third opinion from a test can only
/// manufacture a <c>SourceDisagreement</c> nobody can act on. A projected test's job is evidence.
/// </para>
/// <para>
/// <b>Prefer <see cref="SliceType"/>.</b> It means exactly <c>SliceName = type.Name</c> — no suffix
/// stripping, nothing inferred — so it is rename-safe AND the type survives to the generator, where
/// a string does not. Reach for <see cref="SliceName"/> when no type bears the slice's name, which
/// is every Automation slice: its handler is <c>{Slice}Handler</c> and there is no command type at
/// all.
/// </para>
/// <code>
/// [BobcatFeature("Booking appointments")]
/// [BobcatSlice(SliceType = typeof(ConfirmAppointment), Domain = "Scheduling", Chapter = "BookingAppointments")]
/// public class BookingSpecs
/// {
///     [Fact, BobcatScenario]
///     [BobcatSlice(SliceName = "ProposeHomeCheckAppointment", Pattern = "Automation")]
///     public async Task an_accepted_assignment_proposes_a_visit() { }
/// }
/// </code>
/// <para>
/// A method-level attribute REPLACES the class's for that test, which is how one class covers
/// several slices. It replaces rather than overrides: a rebinding is a whole binding, so a test
/// that wants the class's <see cref="Domain"/> as well restates it. Otherwise rebinding to another
/// slice would stamp this class's groupings onto a slice that already has its own.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class BobcatSliceAttribute : Attribute
{
    /// <summary>The slice name — the merge key the whole pipeline folds on.</summary>
    public string? SliceName { get; set; }

    /// <summary>
    /// The type whose <c>Name</c> IS the slice name, for the 16-in-19 case where one exists: a
    /// Command slice's command record, a View slice's read model.
    /// </summary>
    public Type? SliceType { get; set; }

    /// <summary>Groups slices into sub-diagrams, mirroring the model's <c>domain:</c>.</summary>
    public string? Domain { get; set; }

    /// <summary>The board chapter, mirroring the model's <c>chapter:</c> (issue #298).</summary>
    public string? Chapter { get; set; }

    /// <summary>
    /// The slice's Event Modeling pattern, for the same reason <c>@pattern:</c> exists (issue
    /// #323): a projected test's shape cannot express Automation or Translation, and guessing
    /// Command disagrees with a model that knows better. One of
    /// Command | View | Automation | Translation.
    /// </summary>
    public string? Pattern { get; set; }
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
