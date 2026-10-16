using System.Reflection;
using Bobcat.Engine;
using Shouldly;
using Xunit;
using Xunit.v3;

namespace Bobcat.Xunit.Tests;

/// <summary>
/// Issue #571, under the real runner: with no results directory, a kept attachment lands on
/// xUnit's own <c>TestContext.Attachments</c>, which is what TRX and the IDE show.
/// </summary>
/// <remarks>
/// The check runs from a class-level hook because xUnit runs after-test hooks in reverse — the
/// method's <c>[BobcatScenario]</c> closes first, then this — and the attachment can only exist
/// once the scenario has closed. The scenario passes, so the run is made verbose for the moment it
/// closes; a failed one would keep it anyway, but would also fail this test.
/// </remarks>
[AttachmentsLandOnTheTest]
public class AttachmentTests
{
    [Fact, BobcatScenario]
    public void an_attachment_reaches_the_platforms_attachments()
    {
        ScenarioReportVisibility.Verbose = true;

        using (ScenarioRecorder.Step("Then", "the page is captured"))
        {
            SpecAttachment.Add("page.png", [1, 2, 3], "image/png");
        }
    }
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class AttachmentsLandOnTheTestAttribute : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        ScenarioReportVisibility.Reset();

        var attachments = global::Xunit.TestContext.Current.Attachments.ShouldNotBeNull();
        var attachment = attachments["01-page.png"];
        attachment.AsByteArray().Item1.ShouldBe(new byte[] { 1, 2, 3 });
        attachment.AsByteArray().Item2.ShouldBe("image/png");
    }
}
