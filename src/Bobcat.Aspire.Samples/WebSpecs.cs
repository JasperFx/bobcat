namespace Bobcat.Aspire.Samples;

/// <summary>Specifications over the AppHost's web service, called by its resource name.</summary>
// #region web-specs
[BobcatFeature("Greeting service")]
public sealed class WebSpecs(TestInfrastructure infrastructure)
{
    private readonly AspireHttpSteps _http = new(infrastructure);
    private AspireHttpSteps Http => _http;

    [BobcatSpec]
    public async Task the_service_greets_by_name()
    {
        await Http.IsSent("GET", "/greeting/Ada", "web");

        Http.TheStatusIs(200);
        Http.TheBodyIs("Hello, Ada");
    }

    [BobcatSpec]
    public async Task a_job_finishes_after_the_request_returns()
    {
        await Http.IsSent("POST", "/jobs/42", "web");
        Http.TheStatusIs(202);

        await Http.EventuallyAnswers("/jobs/42", "web", 200, 10);
    }
}
// #endregion web-specs
