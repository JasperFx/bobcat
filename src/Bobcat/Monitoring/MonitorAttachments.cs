namespace Bobcat.Monitoring;

/// <summary>
/// The one place a <see cref="RecordedAttachment"/> becomes a <see cref="ScenarioAttachmentInfo"/>
/// (issue #571) — the sibling of <see cref="MonitorReports"/>.
/// </summary>
/// <remarks>
/// A reference only — name, media type, size, path — never the bytes: a run that tried to say too
/// much once faulted its worker with a 698MB payload, and the wire is the last place that should be
/// able to happen again.
/// </remarks>
public static class MonitorAttachments
{
    /// <summary>The kept attachments, or <b>null</b> when there are none.</summary>
    public static IReadOnlyList<ScenarioAttachmentInfo>? From(IReadOnlyList<RecordedAttachment> attachments)
    {
        if (attachments.Count == 0) return null;

        return attachments
            .Select(x => new ScenarioAttachmentInfo(x.Name, x.MediaType, x.Size, x.FullPath, x.StepId, x.Omitted))
            .ToList();
    }
}
