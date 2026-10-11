using Bobcat;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// One call, several sentences (bobcat#457). A paragraph that only asserts reads "Then … / And …"; one
/// that arranges, acts and asserts changes keyword as it goes (wolverine#4938), and repeating the
/// keyword it is already in reads "And".
/// </summary>
public class ParagraphTests
{
    private static ScenarioRecorder.Recording begin()
        => ScenarioRecorder.Begin("Feature", "Scenario", null, Guid.NewGuid());

    [Fact]
    public void every_sentence_after_the_first_reads_and()
    {
        using var recording = begin();
        var paragraph = new Paragraph("Then");
        using (paragraph.Sentence("a Wallet stream is started")) { }
        using (paragraph.Sentence("WalletOpened is emitted on it")) { }

        recording.Steps.Select(x => x.Keyword).ShouldBe(["Then", "And"]);
    }

    [Fact]
    public void a_sentence_may_change_the_keyword_and_a_repeated_one_reads_and()
    {
        using var recording = begin();
        var paragraph = new Paragraph("Given");
        using (paragraph.Sentence("the Wallet stream is at version 2")) { }
        using (paragraph.Sentence("When", "the wallet is credited")) { }
        using (paragraph.Sentence("Then", "WalletCredited is emitted on it")) { }
        using (paragraph.Sentence("then", "nothing else is")) { }
        using (paragraph.Sentence("the balance is 40")) { }

        recording.Steps.Select(x => $"{x.Keyword} {x.Text}").ShouldBe([
            "Given the Wallet stream is at version 2",
            "When the wallet is credited",
            "Then WalletCredited is emitted on it",
            "And nothing else is",
            "And the balance is 40"
        ]);
    }

    [Fact]
    public void the_first_sentence_may_name_its_own_keyword()
    {
        using var recording = begin();
        var paragraph = new Paragraph("Given");
        using (paragraph.Sentence("When", "the wallet is credited")) { }

        recording.Steps.Single().Keyword.ShouldBe("When");
    }

    [Fact]
    public void outside_a_scenario_a_paragraph_records_nothing()
    {
        var paragraph = new Paragraph("Given");
        using (paragraph.Sentence("When", "the wallet is credited")) { }

        ScenarioRecorder.Current.ShouldBeNull();
    }
}
