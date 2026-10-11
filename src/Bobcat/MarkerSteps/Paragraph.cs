namespace Bobcat;

/// <summary>
/// One call that reads as several sentences, Storyteller's paragraph: <c>ThenStreamIsStartedWithEvents&lt;T&gt;(...)</c>
/// is "Then a VolunteerApplication stream is started" and "And VolunteerApplicationSubmitted is emitted on it".
/// Each sentence is a step of its own, with its own verdict, so a failure says which one.
/// </summary>
/// <example>
/// <code>
/// var paragraph = new Paragraph("Then");
/// using (paragraph.Sentence("a VolunteerApplication stream is started")) { ... }
/// using (paragraph.Sentence("VolunteerApplicationSubmitted is emitted on it")) { ... }
/// </code>
/// A paragraph that arranges, acts and asserts changes keyword as it goes, and a sentence repeating
/// the keyword before it reads "And" (wolverine#4938, <c>CritterStackFixture.WhenActingOn</c>):
/// <code>
/// var paragraph = new Paragraph("Given");
/// using (paragraph.Sentence("the Wallet stream is at version 2")) { ... }
/// using (paragraph.Sentence("When", "the wallet is credited")) { ... }
/// using (paragraph.Sentence("Then", "WalletCredited is emitted on it")) { ... }
/// </code>
/// </example>
public sealed class Paragraph
{
    private string _keyword;
    private bool _first = true;

    /// <param name="keyword">The first sentence's keyword; every later one reads "And" unless it names its own.</param>
    public Paragraph(string keyword)
    {
        _keyword = keyword;
    }

    /// <summary>The next sentence, open until the returned handle is disposed.</summary>
    public IDisposable Sentence(string text)
    {
        var keyword = _first ? _keyword : "And";
        _first = false;
        return ScenarioRecorder.Step(keyword, text);
    }

    /// <summary>
    /// The next sentence under <paramref name="keyword"/> — "When" after a "Given", "Then" after a
    /// "When" — or "And" when that is the keyword the paragraph is already speaking in.
    /// </summary>
    public IDisposable Sentence(string keyword, string text)
    {
        if (!_first && string.Equals(keyword, _keyword, StringComparison.OrdinalIgnoreCase)) return Sentence(text);

        _keyword = keyword;
        _first = false;
        return ScenarioRecorder.Step(keyword, text);
    }
}
