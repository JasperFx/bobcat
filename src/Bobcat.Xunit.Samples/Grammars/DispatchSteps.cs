using System.Text;
using Bobcat.Engine;

namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// The telemetry sample behind <c>docs/failure-telemetry.md</c>: a dispatcher that hands parcels to
/// carriers, with the account of what it did in a scenario report (one row per parcel, a red row for a
/// refusal), a step log line per decision, and the manifest as an attachment when a step fails.
/// </summary>
// #region dispatch-report
public sealed class DispatchReport : TableReport
{
    public override string Title => "Dispatches";

    public void Dispatched(string parcel, string carrier)
        => Row(("parcel", parcel), ("carrier", carrier), ("status", "dispatched"));

    /// <summary>A judged red row inside an informational table: the parcel the carrier refused.</summary>
    public void Refused(string parcel, string carrier, string reason)
        => Row([
            new CellResult("parcel", ResultStatus.ok, parcel),
            new CellResult("carrier", ResultStatus.ok, carrier),
            new CellResult("status", ResultStatus.failed) { Expected = "dispatched", Actual = "refused", Note = reason }
        ]);
}
// #endregion dispatch-report

// #region dispatch-capture
/// <summary>Attaches the dispatcher's manifest to the step that failed. Registered once, below.</summary>
public sealed class DispatchManifestCapture : IFailureCapture
{
    public Task<IReadOnlyList<CapturedAttachment>> CaptureAsync(FailedStep step, CancellationToken cancellation)
    {
        var manifest = DispatchSteps.Current?.Manifest();
        if (manifest is null) return Task.FromResult<IReadOnlyList<CapturedAttachment>>([]);

        return Task.FromResult<IReadOnlyList<CapturedAttachment>>(
            [new CapturedAttachment("dispatch-manifest.txt", Encoding.UTF8.GetBytes(manifest), "text/plain")]);
    }
}
// #endregion dispatch-capture

public class DispatchSteps
{
    private static readonly AsyncLocal<DispatchSteps?> current = new();

    // #region dispatch-capture-registration
    static DispatchSteps() => SpecAttachment.RegisterFailureCapture(new DispatchManifestCapture());
    // #endregion dispatch-capture-registration

    private readonly List<string> _queued = [];
    private readonly List<(string Parcel, string Carrier, bool Accepted)> _decisions = [];

    public DispatchSteps() => current.Value = this;

    internal static DispatchSteps? Current => current.Value;

    [Given("parcels {parcels} are queued")]
    internal void ParcelsAreQueued(string parcels)
        => _queued.AddRange(parcels.Split(',', StringSplitOptions.TrimEntries));

    // #region dispatch-when
    [When("the dispatcher runs")]
    internal void TheDispatcherRuns()
    {
        foreach (var parcel in _queued)
        {
            // A real dispatcher would ask the carrier; here, any parcel bound overseas is refused
            var carrier = parcel.EndsWith("-INTL") ? "AirFreight" : "RoadRunner";
            var accepted = carrier == "RoadRunner";
            _decisions.Add((parcel, carrier, accepted));

            SpecLog.Write($"{parcel} -> {carrier}: {(accepted ? "accepted" : "refused")}");

            if (accepted) SpecReport.For<DispatchReport>().Dispatched(parcel, carrier);
            else SpecReport.For<DispatchReport>().Refused(parcel, carrier, "no customs paperwork on file");
        }
    }
    // #endregion dispatch-when

    [Then("{count} parcels are dispatched")]
    internal void ParcelsAreDispatched(int count)
        => SpecAssert.Check("count", _decisions.Count(x => x.Accepted), count);

    internal string Manifest()
        => string.Join("\n", _decisions.Select(d => $"{d.Parcel}\t{d.Carrier}\t{(d.Accepted ? "ACCEPTED" : "REFUSED")}"));
}
