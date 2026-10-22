namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The <c>docs/marker-steps.md</c> example of a helper of another keyword ending a marker comment's
/// region (bobcat#682): under <c>// Given the AppHost is running</c>, a <c>[When]</c> helper is not part
/// of the Given. Green.
/// </summary>
[BobcatFeature("Console host")]
public class ConsoleHostSpecs
{
    private bool _running;
    private int _status;

    [Given("the AppHost is started")]
    internal Task StartHost()
    {
        _running = true;
        return Task.CompletedTask;
    }

    [When("the about page is read")]
    internal Task WhenTheAboutPageIsRead()
    {
        _status = _running ? 200 : 503;
        return Task.CompletedTask;
    }

    [Then("the console answers {status}")]
    internal void ThenTheConsoleAnswers(int status) => SpecAssert.Check("status", _status, status);

    [Fact]
    public async Task the_about_page_answers()
    {
        // #region another-keyword-ends-the-region
        // Given the AppHost is running
        await StartHost();                  // [Given] -- nested under the comment
        await WhenTheAboutPageIsRead();     // [When]  -- ends the region, renders on its own
        ThenTheConsoleAnswers(200);         // [Then]  -- on its own too
        // #endregion another-keyword-ends-the-region
    }
}
