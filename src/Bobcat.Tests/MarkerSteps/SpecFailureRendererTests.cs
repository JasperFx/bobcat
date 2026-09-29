using Bobcat;
using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// How a failure is reported is pluggable, and an assertion library whose message already carries an
/// expected/actual pair can hand it over as a cell.
/// </summary>
/// <remarks>
/// Bobcat cannot reference the assertion library a projected suite happens to use, so what
/// <c>ShouldAssertException</c> means — and how to read it — has to be registerable from outside a
/// hard-coded list. The payoff is the narrated lane: a test asserting with Shouldly could only ever
/// report a coarse verdict, three grey sentences and a five-line wall of text with no cell anywhere.
/// </remarks>
public class SpecFailureRendererTests
{
    [Fact]
    public void a_shouldly_message_becomes_an_expected_actual_cell()
    {
        var failure = SpecFailureRenderers.Render(new SpecFailureContext(
            "ShouldAssertException",
            "calculator.Value\n    should be\n7d\n    but was\n6d"));

        failure.Kind.ShouldBe(SpecFailureKind.Assertion);
        failure.ShowStackTrace.ShouldBeFalse();

        var cell = failure.Cells.ShouldHaveSingleItem();

        // Shouldly's own subject expression is a better cell name than anything Bobcat could invent,
        // and is the whole reason its messages read well.
        cell.Name.ShouldBe("calculator.Value");
        cell.Expected.ShouldBe("7d");
        cell.Actual.ShouldBe("6d");
        cell.Status.ShouldBe(ResultStatus.failed);
    }

    [Fact]
    public void a_shouldly_message_it_cannot_parse_falls_back_to_the_message()
    {
        // Shouldly has many message shapes and this reads one of them. Never worse than today's output.
        var failure = SpecFailureRenderers.Render(new SpecFailureContext(
            "ShouldAssertException", "Task should complete in 2 seconds but did not"));

        failure.Kind.ShouldBe(SpecFailureKind.Assertion);
        failure.Cells.ShouldBeEmpty();
        failure.Message.ShouldBe("Task should complete in 2 seconds but did not");
    }

    [Fact]
    public void an_unrecognised_assertion_library_is_still_an_assertion_by_convention()
    {
        // No registration needed for a library that follows the naming convention: a wrong, with its
        // message and no stack.
        var failure = SpecFailureRenderers.Render(
            new SpecFailureContext("SomeHouseLibraryAssertionException", "nope"));

        failure.Kind.ShouldBe(SpecFailureKind.Assertion);
        failure.ShowStackTrace.ShouldBeFalse();
    }

    [Fact]
    public void anything_else_is_an_error_with_its_stack()
    {
        var failure = SpecFailureRenderers.Render(
            new SpecFailureContext("InvalidOperationException", "the naming service is down"));

        failure.Kind.ShouldBe(SpecFailureKind.Error);

        // Over-stating severity is the safe direction: an assertion nobody recognised shows as an
        // error, which is visible and wrong the right way round.
        failure.ShowStackTrace.ShouldBeTrue();
    }

    [Fact]
    public void a_registration_wins_over_the_convention_and_can_be_taken_back_out()
    {
        var context = new SpecFailureContext("SomeHouseLibraryAssertionException", "nope");

        using (SpecFailureRenderers.Register(new AlwaysAnError()))
        {
            SpecFailureRenderers.Render(context).Kind.ShouldBe(SpecFailureKind.Error);
        }

        // Reversible because the registry is process-wide: a test that installed one and could not take
        // it out would change how every later test in the process reads its failures.
        SpecFailureRenderers.Render(context).Kind.ShouldBe(SpecFailureKind.Assertion);
    }

    [Fact]
    public void the_most_recent_registration_claims_a_type_first()
    {
        var context = new SpecFailureContext("ShouldAssertException", "x\n    should be\n1\n    but was\n2");

        using (SpecFailureRenderers.Register(new ShoutyShouldly()))
        {
            SpecFailureRenderers.Render(context).Message.ShouldBe("SHOUTY");
        }

        SpecFailureRenderers.Render(context).Cells.ShouldHaveSingleItem();
    }

    private sealed class AlwaysAnError : ISpecFailureRenderer
    {
        public bool Handles(string exceptionTypeName) => exceptionTypeName.Contains("HouseLibrary");
        public SpecFailure Render(SpecFailureContext context) => SpecFailure.Error(context.Message);
    }

    private sealed class ShoutyShouldly : ISpecFailureRenderer
    {
        public bool Handles(string exceptionTypeName) => exceptionTypeName == "ShouldAssertException";
        public SpecFailure Render(SpecFailureContext context) => SpecFailure.Assertion("SHOUTY");
    }
}
