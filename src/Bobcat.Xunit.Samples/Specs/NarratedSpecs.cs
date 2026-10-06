using Bobcat.Xunit.Samples.Application;
using Bobcat.Xunit.Samples.Grammars;
using Shouldly;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The other projected authoring style: <b>marker comments</b>, which narrate a test that already
/// exists and asserts with its own library.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here calls a grammar. The steps come from the comments, which the compiler erases and the
/// generator reads — so opting an existing suite in costs one class attribute and some comments, with
/// no change to a single line of test logic. That is the adoption story the marker lane was built
/// for, and it is worth reading beside <see cref="CalculatorSpecs"/>: same behaviour, same
/// specification, two very different reports.
/// </para>
/// <para>
/// <b>What the comment lane cannot do.</b> A comment DECLARES a step; it does not execute one, so
/// there is no step object for a value comparison to attach to and no cell in the report. The finest
/// verdict available is the test's own exception, which means one failure per test and no
/// expected/actual pair. <see cref="a_narrated_value_check_that_disagrees"/> is what that looks like.
/// </para>
/// </remarks>
[BobcatFeature("Calculator, narrated"), BobcatScenario]
public class NarratedSpecs
{
    [Fact]
    public void using_sentences()
    {
        // Given a calculator starting with 3
        var calculator = new Calculator { Value = 3 };

        // When it is multiplied by 2
        calculator.MultiplyBy(2);

        // Then the value should be 6
        calculator.Value.ShouldBe(6);
    }

    /// <summary>
    /// The same specification with the wrong expectation. Shouldly throws, so the report carries the
    /// scenario's failure and nothing narrower — no cell, no expected/actual, and the steps are
    /// declared but never recorded.
    /// </summary>
    [Fact]
    public void a_narrated_value_check_that_disagrees()
    {
        // Given a calculator starting with 3
        var calculator = new Calculator { Value = 3 };

        // When it is multiplied by 2
        calculator.MultiplyBy(2);

        // Then the value should be 7
        calculator.Value.ShouldBe(7);
    }

    /// <summary>
    /// Both styles at once, which is how a real suite adopts this: comments for the narrative a
    /// reader wants, grammar helpers for the steps worth measuring and colouring. The recorded steps
    /// nest under the comment they ran inside — a compile-time join on the call site's line, never a
    /// guess from timing.
    /// </summary>
    [Fact]
    public void narrated_with_grammar_steps_inside()
    {
        var grammar = new CalculatorGrammar();

        // Given a calculator with a value of 3 that is then doubled
        grammar.StartWith(3);
        grammar.MultiplyBy(2);

        // Then the arithmetic holds
        grammar.TheValueShouldBe(6);
        grammar.AddingNumbersTogether(2, 3, 5);
    }

    /// <summary>The same mixture, failing inside the second comment.</summary>
    [Fact]
    public void narrated_with_a_wrong_grammar_step_inside()
    {
        var grammar = new CalculatorGrammar();

        // Given a calculator with a value of 3 that is then doubled
        grammar.StartWith(3);
        grammar.MultiplyBy(2);

        // Then the arithmetic holds
        grammar.TheValueShouldBe(7);
        grammar.AddingNumbersTogether(2, 3, 6);
    }

    /// <summary>
    /// A test that opts in and narrates nothing: no comments, no grammar calls. It renders as a
    /// scenario with no steps at all, which Bobcat treats elsewhere as a pending specification.
    /// </summary>
    [Fact]
    public void a_test_that_declares_nothing()
    {
        new Calculator { Value = 1 }.Value.ShouldBe(1);
    }

    /// <summary>
    /// A RUN of consecutive assertions all get evaluated, and the run's failures are thrown at its end.
    /// </summary>
    /// <remarks>
    /// Four assertions, two of them wrong. A plain Shouldly test reports the first and leaves three
    /// blanks; here all four reach the report and the test still fails, once.
    /// </remarks>
    [Fact]
    public void every_assertion_in_a_run_is_evaluated()
    {
        var calculator = new Calculator { Value = 3 };

        // Then the calculator agrees about its value
        calculator.Value.ShouldBe(3);
        calculator.Value.ShouldBeGreaterThan(10);
        calculator.Value.ShouldBeLessThan(2);
        calculator.Value.ShouldBe(3);
    }

    /// <summary>
    /// The run ends at the next ACTION, which never runs — it would be operating on state the
    /// assertions have already shown to be wrong, and anything it reported after that is noise.
    /// </summary>
    [Fact]
    public void the_run_throws_before_the_next_action()
    {
        var calculator = new Calculator { Value = 3 };

        // Then the value disagrees
        calculator.Value.ShouldBe(99);

        // When the calculator is doubled anyway
        calculator.MultiplyBy(2);

        // Then this is never reached
        calculator.Value.ShouldBe(6);
    }

    /// <summary>
    /// Issue #384: a cell says what it actually compared, so a non-equality assertion cannot state
    /// an equality it never checked.
    /// </summary>
    /// <remarks>
    /// Each of these used to render <c>expected 'N', got '3'</c> — a claim about equality, and false
    /// for every one of them, because N is a bound rather than an expectation. The sentence was
    /// always right; only the cell lied, which is why it survived so long.
    /// </remarks>
    [Fact]
    public void a_cell_says_which_comparison_it_made()
    {
        var calculator = new Calculator { Value = 3 };

        // Then the value fails four different comparisons
        calculator.Value.ShouldBeGreaterThan(10);
        calculator.Value.ShouldBeLessThan(2);
        calculator.Value.ShouldNotBe(3);
        calculator.Value.ShouldBe(3.5, 0.01);
    }

    /// <summary>
    /// Issue #384 acceptance 4: an assertion outside the closed set renders as a plain step line and
    /// produces no cell at all.
    /// </summary>
    /// <remarks>
    /// <c>ShouldBeTrue</c>'s subject IS its claim, so there is no expected/actual pair any row shape
    /// could state truthfully. The honest degradation is to say nothing — the step still carries its
    /// verdict and its duration, and the closed enum is what makes that the only option available
    /// rather than one choice among several.
    /// </remarks>
    [Fact]
    public void an_assertion_bobcat_cannot_describe_still_renders_as_a_step()
    {
        var calculator = new Calculator { Value = 3 };

        // Then a comparison outside the closed set reports no cell
        (calculator.Value > 10).ShouldBeTrue();
    }
}
