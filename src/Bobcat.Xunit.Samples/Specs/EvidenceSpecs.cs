using System.Text;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The <c>docs/marker-steps.md</c> attachment example (issue #571): bytes, text and a file hung on the
/// step in progress. Green, so the run keeps none of them unless it is verbose — which is the latch the
/// page describes, not a sign the calls did nothing.
/// </summary>
[BobcatFeature("Failure evidence"), BobcatScenario]
public class EvidenceSpecs
{
    private string _response = "";

    [When("the order is placed")]
    internal void TheOrderIsPlaced()
    {
        _response = """{ "order": 42, "status": "accepted" }""";

        var bytes = Encoding.UTF8.GetBytes(_response);
        var consoleOutput = "placing order 42\naccepted";
        var screenshotPath = Path.Combine(Path.GetTempPath(), $"bobcat-sample-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(screenshotPath, [0x89, 0x50, 0x4E, 0x47]);

        // #region attachments
        SpecAttachment.Add("response.json", bytes, "application/json");
        SpecAttachment.AddText("console.log", consoleOutput);
        SpecAttachment.AddFile(screenshotPath);          // read now; media type from the extension
        // #endregion attachments

        File.Delete(screenshotPath);
    }

    [Then("the order is accepted")]
    internal void TheOrderIsAccepted() => SpecAssert.Fact(_response.Contains("accepted"), "the response says accepted");

    [Fact]
    public void an_order_is_placed()
    {
        TheOrderIsPlaced();
        TheOrderIsAccepted();
    }
}
