using Bobcat.Xunit.Samples.Grammars;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// Storyteller 5's <c>StoryTeller.Samples/Specs/Sentences</c> suite — keywordless sentences, and
/// prose between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Storyteller's prose paragraphs map onto marker comments.</b> <c>Sentences.md</c> has plain
/// text between groups of steps — "Work correctly", "Correct assertion", "Line assertions" — which
/// Storyteller rendered as comments in the specification. Here they are marker comments, and the
/// grammar calls nest under them. A keywordless comment opens with <c>*</c>, because an ordinary
/// comment has to stay an ordinary comment.
/// </para>
/// </remarks>
[BobcatFeature("Sentences"), BobcatScenario]
public class SentenceSpecs
{
    private readonly SentenceGrammar _sentence = new();

    /// <summary>
    /// Storyteller: <c>Specs/Sentences/Sentences.md</c>, minus the first step. The original opens
    /// with <c>StartWithTheNumber number=a</c> — the letter "a" where an <c>int</c> belongs — to show
    /// a cell that cannot be converted. A projected test has no cell text to mis-convert: the
    /// argument is C#, so that failure mode is a compile error instead, which is strictly better and
    /// simply has no rendering.
    /// </summary>
    [Fact]
    public void sentences()
    {
        // * Work correctly
        _sentence.StartWithTheNumber(5);
        _sentence.MultiplyThenAdd(3, 4);
        _sentence.Subtract(2);

        // * Correct assertion
        _sentence.TheValueShouldBe(17);

        // * Incorrect assertion
        _sentence.TheSumOf(2, 2, 5);

        // * Line assertions
        _sentence.ThisLineIsAlwaysTrue();
        _sentence.ThisLineIsAlwaysFalse();
        _sentence.XplusYShouldBe(2, 2, 5);
    }

    /// <summary>
    /// The same specification ending in Storyteller's throwing line, so the steps after it render as
    /// never reached rather than vanishing.
    /// </summary>
    [Fact]
    public void sentences_that_stop_at_an_exception()
    {
        // * Work correctly
        _sentence.StartWithTheNumber(5);
        _sentence.MultiplyThenAdd(3, 4);

        // * Line assertions
        _sentence.ThisLineAlwaysThrowsExceptions();
        _sentence.ThisLineIsAlwaysTrue();
        _sentence.TheValueShouldBe(19);
    }

    /// <summary>All green, keywordless throughout — the shape Storyteller specs mostly had.</summary>
    [Fact]
    public void arithmetic_holds()
    {
        _sentence.StartWithTheNumber(5);
        _sentence.MultiplyThenAdd(3, 4);
        _sentence.Subtract(2);
        _sentence.TheValueShouldBe(17);
        _sentence.TheSumOf(2, 2, 4);
        _sentence.XplusYShouldBe(2, 2, 4);
    }
}
