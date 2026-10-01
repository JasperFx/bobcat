using Bobcat;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// The comment-to-step rule, and the one case it deliberately refuses: prose that happens to begin
/// with <c>And</c> or <c>But</c>.
/// </summary>
/// <remarks>
/// <para>
/// This lane's whole promise is that an existing suite adopts it by writing sentences, so the rule for
/// "is this comment a step" has to be right in both directions. Too loose and switching it on turns
/// notes to a reader into specification steps — and worse, wraps the real steps underneath them,
/// because a narrative row nests whatever ran inside it.
/// </para>
/// <para>
/// Deliberately does NOT clear <c>DeclaredSteps</c>: this registry is populated once by the generated
/// module initializer and nothing here adds to it, so clearing it would delete the very facts the
/// other tests in this class read.
/// </para>
/// </remarks>
[BobcatFeature("Marker comment tightening")]
public class MarkerCommentTighteningTests
{
    [Fact]
    public void prose_that_opens_with_and_is_not_a_step()
    {
        // And this reads as a note to whoever maintains the test
        // But so does this one
        // And so does this
        var declared = DeclaredSteps.For("Marker comment tightening/prose that opens with and is not a step");

        // Nothing opened a narrative, so all three are ordinary comments. This is the exact shape that
        // went wrong in FactSpecs: `// And a false one fails its step` was a note to a reader, became a
        // step, and wrapped the two real steps under a narrative row nobody wrote.
        declared.ShouldBeEmpty();
    }

    [Fact]
    public void and_continues_a_narrative_that_is_open()
    {
        // Given an open narrative
        // And a second step continuing it
        // But a third, still continuing it
        var declared = DeclaredSteps.For("Marker comment tightening/and continues a narrative that is open");

        declared.Select(x => x.Keyword).ShouldBe(["Given", "And", "But"]);
    }

    [Fact]
    public void a_bullet_opens_a_narrative_too()
    {
        // * a keywordless step, Gauge style
        // And one continuing it
        var declared = DeclaredSteps.For("Marker comment tightening/a bullet opens a narrative too");

        declared.Select(x => x.Keyword).ShouldBe(["", "And"]);
    }

    [Fact]
    public void once_a_narrative_is_open_an_and_comment_is_a_step_whatever_it_says()
    {
        // Given a narrative that is open
        // And now every comment opening with And is a step, including this one
        var declared = DeclaredSteps.For(
            "Marker comment tightening/once a narrative is open an and comment is a step whatever it says");

        // The honest limit of the rule, pinned rather than left to be discovered. Inside a test that is
        // already narrating, `And ...` is taken at its word — there is no way to tell that sentence from
        // a step, and guessing from its wording would be worse than a rule an author can learn.
        declared.Count.ShouldBe(2);
        declared[1].Keyword.ShouldBe("And");
    }

    [Fact]
    public void a_keyword_that_is_only_the_start_of_a_word_is_not_a_step()
    {
        // Given a narrative
        // Andrew reviewed this test and had no notes
        var declared = DeclaredSteps.For(
            "Marker comment tightening/a keyword that is only the start of a word is not a step");

        declared.Count.ShouldBe(1);
    }
}
