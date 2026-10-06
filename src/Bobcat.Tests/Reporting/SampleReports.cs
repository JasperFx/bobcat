using Bobcat.Engine;

namespace Bobcat.Tests.Reporting;

/// <summary>
/// The worked example from issue #408: the account a tracked messaging session can give of a
/// scenario. Shaped on Wolverine's <c>EnvelopeRecord</c> — event type, message, destination,
/// attempt and the session offset — so the test corpus exercises the real case rather than a
/// contrived one.
/// </summary>
public class MessageActivityReport : TableReport
{
    public override string Title => "Message activity";

    public override string? ShortTitle => "Messages";

    /// <summary>One envelope record, stating what happened and judging nothing.</summary>
    public void Record(long atMs, string envelopeEvent, string message, string? destination, int attempt)
        => Row(("at (ms)", atMs), ("event", envelopeEvent), ("message", message),
            ("destination", destination), ("attempt", attempt));

    /// <summary>
    /// The one row that IS a claim — an envelope that was dead-lettered, red inside an otherwise
    /// informational table, which is the case that would otherwise want a second mechanism.
    /// </summary>
    public void DeadLettered(long atMs, string message, string destination, int attempt)
        => Row([
            new CellResult("at (ms)", ResultStatus.ok, atMs.ToString()),
            new CellResult("event", ResultStatus.failed)
            {
                Expected = "MessageSucceeded", Actual = "MovedToErrorQueue"
            },
            new CellResult("message", ResultStatus.ok, message),
            new CellResult("destination", ResultStatus.ok, destination),
            new CellResult("attempt", ResultStatus.ok, attempt.ToString())
        ]);
}

/// <summary>A report that insists on being written out whatever the verdict.</summary>
public class AlwaysReport : TableReport
{
    public override string Title => "Always";
    public override ReportVisibility Visibility => ReportVisibility.Always;

    public void Add(string what) => Row(("what", what));
}

/// <summary>A report with a cap of three, so the suppression rule is testable without 200 rows.</summary>
public class CappedReport : TableReport
{
    public override string Title => "Capped";
    public override int MaxRows => 3;

    public void Add(int n) => Row(("n", n));
}

/// <summary>
/// The producer both lanes run, written the way a grammar in another package has to be written:
/// against the static <see cref="SpecReport"/> surface, with no <see cref="IStepContext"/> and no
/// knowledge of which lane it is in.
/// </summary>
public static class SharedProducer
{
    public static void Report()
    {
        var report = SpecReport.For<MessageActivityReport>();

        report.Record(0, "Sent", "ConfirmAppointment", "local://confirm", 1);
        report.Record(3, "Received", "ConfirmAppointment", "local://confirm", 1);
        report.Record(19, "MessageSucceeded", "ConfirmAppointment", null, 1);
        report.DeadLettered(104, "NotifyPatient", "rabbitmq://notify", 3);
    }
}
