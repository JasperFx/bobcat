using Bobcat;
using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// An assertion disagreeing and code breaking are different failures, and a projected step has to
/// tell them apart by exception type NAME — Bobcat cannot reference the assertion library a
/// projected suite happens to use.
/// </summary>
public class ProjectedFailureTests
{
    private sealed class ShouldAssertException(string message) : Exception(message);

    private class XunitException(string message) : Exception(message);

    private sealed class AssertionException(string message) : Exception(message);

    private sealed class AssertFailedException(string message) : Exception(message);

    private sealed class EqualException(string message) : XunitException(message);

    [Fact]
    public void bobcats_own_assertion_exception_is_a_wrong()
        => ProjectedFailure.StatusOf(new SpecAssertionException("nope")).ShouldBe(ResultStatus.failed);

    [Theory]
    [InlineData(typeof(ShouldAssertException))]   // Shouldly
    [InlineData(typeof(XunitException))]          // xUnit
    [InlineData(typeof(AssertionException))]      // NUnit, TUnit
    [InlineData(typeof(AssertFailedException))]   // MSTest
    public void every_mainstream_assertion_library_is_recognised(Type failure)
        => ProjectedFailure.IsAssertion((Exception)Activator.CreateInstance(failure, "nope")!)
            .ShouldBeTrue();

    [Fact]
    public void a_librarys_own_subclass_comes_along_for_free()
        // Matched as a suffix on every type in the chain, so xUnit's EqualException — and anything
        // else a library derives — needs no entry of its own.
        => ProjectedFailure.IsAssertion(new EqualException("nope")).ShouldBeTrue();

    [Fact]
    public void an_ordinary_exception_is_an_error()
        => ProjectedFailure.StatusOf(new InvalidOperationException("boom")).ShouldBe(ResultStatus.error);

    [Fact]
    public void an_unrecognised_library_reports_an_error_rather_than_a_pass()
    {
        // The rule degrades by OVER-stating severity: an assertion Bobcat could not recognise reads
        // as an error, which is visible and wrong in the safe direction. Reading it as a pass would
        // not be.
        ProjectedFailure.StatusOf(new Exception("a library nobody has heard of"))
            .ShouldBe(ResultStatus.error);
    }

    [Fact]
    public void nothing_thrown_is_not_an_assertion()
        => ProjectedFailure.IsAssertion((Exception?)null).ShouldBeFalse();

    [Fact]
    public void a_name_alone_answers_the_same_question()
    {
        // A verdict that crossed a process boundary is a name and a message; the registry keys on the
        // name for exactly that reason.
        ProjectedFailure.IsAssertion("ShouldAssertException").ShouldBeTrue();
        ProjectedFailure.IsAssertion("SpecAssertionException").ShouldBeTrue();
        ProjectedFailure.IsAssertion("InvalidOperationException").ShouldBeFalse();
        ProjectedFailure.IsAssertion((string?)null).ShouldBeFalse();
    }
}
