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
/// </example>
public sealed class Paragraph
{
    private readonly string _keyword;
    private bool _first = true;

    /// <param name="keyword">The first sentence's keyword; every later one reads "And".</param>
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
}
