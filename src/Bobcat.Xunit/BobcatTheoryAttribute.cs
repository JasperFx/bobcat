using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.v3;

namespace Bobcat.Xunit;

/// <summary>
/// The one-attribute form of a data-driven projected specification (bobcat#569): it IS the
/// <see cref="TheoryAttribute"/>, it opens the scenario recording, and it carries the Event Modeling
/// slice binding — <see cref="BobcatSpecAttribute"/> for a theory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each row is its own specification.</b> A row's identity is the method's plus the row:
/// <c>Discounts/Discount is applied (amount: 100, tier: Gold)</c>, or
/// <c>Discounts/Discount is applied [gold customer]</c> for a <see cref="BobcatExampleAttribute"/>.
/// The ledger, a run by identity and <c>scenario_finished</c> see each row; the Event Model keeps one
/// specification per method, and each row's result names it as <c>ParentSpecId</c>.
/// </para>
/// <para>
/// It works the way <see cref="BobcatSpecAttribute"/> does — the #403 trick: xUnit collects
/// before/after hooks by the <see cref="IBeforeAfterTestAttribute"/> interface, so a
/// <see cref="TheoryAttribute"/> subclass can be the bracket too. The caller parameters are
/// re-declared and forwarded for the same reason, and a subclass inherits that obligation.
/// </para>
/// <para>
/// Constant rows (<c>[InlineData]</c>, <c>[BobcatExample]</c>) are listed in the spec manifest at
/// compile time. Rows from a <c>[MemberData]</c> or <c>[ClassData]</c> still get their own identities
/// when they run, but cannot be listed ahead of the run.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class BobcatTheoryAttribute : TheoryAttribute, IBeforeAfterTestAttribute, IExplicitScenarioName
{
    /// <param name="sourceFilePath">Filled in by the compiler; never pass this.</param>
    /// <param name="sourceLineNumber">Filled in by the compiler; never pass this.</param>
    public BobcatTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
    }

    /// <param name="sliceType">
    /// The type whose <c>Name</c> IS the slice name — a Command slice's command record, a View
    /// slice's read model. Means exactly <see cref="SliceName"/><c> = sliceType.Name</c>: no suffix
    /// stripping, nothing inferred.
    /// </param>
    /// <param name="sourceFilePath">Filled in by the compiler; never pass this.</param>
    /// <param name="sourceLineNumber">Filled in by the compiler; never pass this.</param>
    public BobcatTheoryAttribute(
        Type sliceType,
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
        => SliceType = sliceType;

    /// <inheritdoc cref="BobcatSliceAttribute.SliceName"/>
    public string? SliceName { get; set; }

    /// <inheritdoc cref="BobcatSliceAttribute.SliceType"/>
    public Type? SliceType { get; set; }

    /// <inheritdoc cref="BobcatSliceAttribute.Domain"/>
    public string? Domain { get; set; }

    /// <inheritdoc cref="BobcatSliceAttribute.Chapter"/>
    public string? Chapter { get; set; }

    /// <inheritdoc cref="BobcatSliceAttribute.Pattern"/>
    public string? Pattern { get; set; }

    /// <inheritdoc cref="BobcatSliceAttribute.TriggeredBy"/>
    public string? TriggeredBy { get; set; }

    /// <inheritdoc cref="BobcatSpecAttribute.Name"/>
    public string? Name { get; set; }

    /// <inheritdoc cref="BobcatSpecAttribute.Pending"/>
    public bool Pending
    {
        get => _pending;
        set
        {
            _pending = value;
            if (value && Skip is null) Skip = PendingSkipReason;
        }
    }

    /// <inheritdoc cref="BobcatSpecAttribute.PendingSkipReason"/>
    public const string PendingSkipReason = BobcatSpecAttribute.PendingSkipReason;

    private bool _pending;

    public virtual void Before(MethodInfo methodUnderTest, IXunitTest test)
        => XunitScenarioBracket.Open(methodUnderTest, test);

    public virtual void After(MethodInfo methodUnderTest, IXunitTest test)
        => XunitScenarioBracket.Close();
}
