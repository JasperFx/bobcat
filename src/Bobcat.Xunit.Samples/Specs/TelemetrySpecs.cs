using Bobcat.Xunit.Samples.Grammars;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The end-to-end examples behind <c>docs/failure-telemetry.md</c>: the same steps record a report,
/// step logs and an attachment every time, and the run writes them out only for the specification
/// that fails.
/// </summary>
[BobcatFeature("Dispatch telemetry"), BobcatScenario]
public class TelemetrySpecs
{
    private readonly DispatchSteps _dispatch = new();

    // #region telemetry-passes
    [Fact]
    public void every_parcel_is_dispatched()
    {
        _dispatch.ParcelsAreQueued("P-1, P-2, P-3");
        _dispatch.TheDispatcherRuns();
        _dispatch.ParcelsAreDispatched(3);
    }
    // #endregion telemetry-passes

    // #region telemetry-fails
    [Fact]
    public void a_parcel_the_carrier_refused()
    {
        _dispatch.ParcelsAreQueued("P-1, P-2-INTL, P-3");
        _dispatch.TheDispatcherRuns();
        _dispatch.ParcelsAreDispatched(3);
    }
    // #endregion telemetry-fails
}
