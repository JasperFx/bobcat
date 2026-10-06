using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.v3;

namespace Bobcat.Xunit;

/// <summary>
/// One attribute for a projected xUnit specification (issue #403): it IS the
/// <see cref="FactAttribute"/>, it opens the scenario recording the way
/// <see cref="BobcatScenarioAttribute"/> does, and it carries the Event Modeling slice binding
/// <see cref="BobcatSliceAttribute"/> carries.
/// </summary>
/// <remarks>
/// <para>
/// Three attributes became one:
/// </para>
/// <code>
/// // Before
/// [Fact, BobcatSlice(SliceType = typeof(ConfirmAppointment))]   // plus [BobcatScenario] on the class
///
/// // After
/// [BobcatSpec(typeof(ConfirmAppointment))]
/// </code>
/// <para>
/// <b>Why it can be both a Fact and a before/after hook.</b> xUnit v3 collects test-bracket hooks
/// by the <see cref="IBeforeAfterTestAttribute"/> INTERFACE, not only from the
/// <c>BeforeAfterTestAttribute</c> base class — and those two are siblings, both deriving straight
/// from <see cref="Attribute"/>, so inheriting from both was never an option. Implementing the
/// interface on a <see cref="FactAttribute"/> subclass is what collapses the pair. Verified against
/// xunit.v3 3.2.2 rather than assumed: it is an undocumented-enough detail that
/// <c>BobcatSpecTests</c> pins it.
/// </para>
/// <para>
/// <b>The caller parameters are re-declared and forwarded, and that is not optional.</b>
/// <see cref="FactAttribute"/>'s only constructor is
/// <c>(​[CallerFilePath] string?, [CallerLineNumber] int)</c>, and the compiler fills those in at
/// the call site it sees. A subclass that calls <c>base()</c> without re-declaring them hands the
/// base <b>its own</b> file and line — this file, the line of the <c>base</c> call — so every test
/// in a suite reports the same source location and IDE test navigation lands on the attribute
/// instead of the test. Measured, not reasoned about: forwarding gives the line where
/// <c>[BobcatSpec]</c> is written, exactly as <c>[Fact]</c> does; forgetting gives the line of the
/// <c>base</c> call in the attribute's own source file.
/// </para>
/// <para>
/// <b>It implies <see cref="BobcatScenarioAttribute"/>, which is what closes the BOBCAT028 trap.</b>
/// A class binding a slice with no recording open reaches the Event Model as no specification at
/// all — green tests, an unbound slice, and a diagnostic to explain it. The attribute that claims
/// the slice is now the attribute that opens the recording, so the two cannot come apart.
/// </para>
/// <para>
/// <b>Unsealed, on purpose.</b> <c>[PostgresFact]</c> in this repository is the shape to expect:
/// a suite that gates on an environment subclasses its fact attribute. A subclass of this one
/// inherits the caller-parameter obligation above and must forward them in turn.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class BobcatSpecAttribute : FactAttribute, IBeforeAfterTestAttribute
{
    /// <param name="sourceFilePath">Filled in by the compiler; never pass this.</param>
    /// <param name="sourceLineNumber">Filled in by the compiler; never pass this.</param>
    public BobcatSpecAttribute(
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
    public BobcatSpecAttribute(
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

    public virtual void Before(MethodInfo methodUnderTest, IXunitTest test)
        => XunitScenarioBracket.Open(methodUnderTest);

    public virtual void After(MethodInfo methodUnderTest, IXunitTest test)
        => XunitScenarioBracket.Close();
}
