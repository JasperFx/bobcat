using Bobcat;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// A step that repeats the previous step's keyword renders as <c>And</c>, the way Gherkin has
/// always been written.
/// </summary>
/// <remarks>
/// A <c>[BobcatStep]</c> helper cannot decide this for itself: its keyword is fixed on the
/// attribute, while whether a given call is the first of its block or the third is a fact about the
/// scenario. Before this, a vocabulary that wanted both spellings carried the same helper twice —
/// CritterCrush's store vocabulary had a <c>GivenEvents</c> and a <c>GivenEventsOn</c> differing in
/// nothing but the word they printed.
/// </remarks>
public class RepeatedKeywordTests
{
    private static string[] Render(params (string Keyword, string Text)[] steps)
    {
        using var recording = ScenarioRecorder.Begin("Feature", "Scenario", null, Guid.NewGuid());
        foreach (var (keyword, text) in steps)
        {
            using (ScenarioRecorder.Step(keyword, text)) { }
        }

        return recording.Steps.Select(x => x.Keyword).ToArray();
    }

    [Fact]
    public void a_repeated_keyword_becomes_and()
        => Render(("Given", "a"), ("Given", "b"), ("When", "c"), ("Then", "d"), ("Then", "e"))
            .ShouldBe(["Given", "And", "When", "Then", "And"]);

    [Fact]
    public void a_block_that_repeats_three_times_ands_all_the_way_down()
        => Render(("Given", "a"), ("Given", "b"), ("Given", "c"))
            .ShouldBe(["Given", "And", "And"]);

    [Fact]
    public void an_explicit_and_does_not_close_the_block_it_continues()
        // Given / And / Given is still one Given block, so the third step is an And too. Treating
        // the explicit And as the thing to compare against would reopen the block and print
        // "Given" again in the middle of it.
        => Render(("Given", "a"), ("And", "b"), ("Given", "c"))
            .ShouldBe(["Given", "And", "And"]);

    [Fact]
    public void but_continues_a_block_the_same_way()
        => Render(("Then", "a"), ("But", "b"), ("Then", "c"))
            .ShouldBe(["Then", "But", "And"]);

    [Fact]
    public void returning_to_an_earlier_keyword_opens_it_again()
        // Arrange, act, assert, arrange again is a legitimate shape — a scenario that sets more
        // history up after its first act. That second Given is opening a block, not continuing one.
        => Render(("Given", "a"), ("When", "b"), ("Then", "c"), ("Given", "d"))
            .ShouldBe(["Given", "When", "Then", "Given"]);

    [Fact]
    public void the_first_step_is_never_an_and()
        => Render(("Then", "a")).ShouldBe(["Then"]);
}
