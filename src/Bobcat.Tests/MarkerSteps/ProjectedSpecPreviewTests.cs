using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// bobcat#600: <c>bobcat preview</c> over a suite whose specifications are inherited from a shared
/// base shows each subclass's specification with the steps its base declares, and not the base.
/// </summary>
public class ProjectedSpecPreviewTests
{
    private const string baseUid = "Preview base 600/can request reply";
    private const string tcpUid = "Preview TCP 600/can request reply";
    private const string rabbitUid = "Preview Rabbit 600/can request reply";

    [Fact]
    public void an_inherited_specification_previews_under_each_subclass_with_its_bases_steps()
    {
        // What the generated initializers say: the base's body declares the steps, each subclass binds
        // its own identity and says where its body came from. The abstract base binds nothing.
        DeclaredSteps.Register(baseUid,
            new DeclaredStep("Given", "a transport", 10), new DeclaredStep("Then", "a reply", 11));
        DeclaredSteps.Bind(tcpUid, "Transports.TcpCompliance", "can_request_reply");
        DeclaredSteps.Inherit(tcpUid, baseUid);
        DeclaredSteps.Bind(rabbitUid, "Transports.RabbitCompliance", "can_request_reply");
        DeclaredSteps.Inherit(rabbitUid, baseUid);

        try
        {
            var previews = ProjectedSpecPreview.All();

            var tcp = previews.Single(x => x.FeatureTitle == "Preview TCP 600");
            tcp.Title.ShouldBe("can request reply");
            tcp.Steps.Select(x => x.StepText).ShouldBe(["a transport", "a reply"]);

            previews.ShouldContain(x => x.FeatureTitle == "Preview Rabbit 600");
            previews.ShouldNotContain(x => x.FeatureTitle == "Preview base 600");
        }
        finally
        {
            DeclaredSteps.Remove(baseUid);
            DeclaredSteps.Remove(tcpUid);
            DeclaredSteps.Remove(rabbitUid);
        }
    }
}
