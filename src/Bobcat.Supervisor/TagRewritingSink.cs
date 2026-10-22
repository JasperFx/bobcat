using Bobcat.Monitoring;

namespace Bobcat.Supervisor;

/// <summary>
/// A decorator over an <see cref="IMonitorEventSink"/> that stamps one run's
/// <see cref="RunStarted.Tag"/> with a tag of the caller's choosing, whatever
/// <c>BOBCAT_RUN_TAG</c> says in the process environment (bobcat#768).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MonitorRunInfo.Discover"/> reads <c>BOBCAT_RUN_TAG</c> once per process, so every
/// <see cref="Supervisor"/> that runs inside one process would publish under the same tag — a
/// gate running several test projects from one Nuke process would put N runs on the board all
/// claiming the same plan node. This is the seam that lets each of them speak for its own:
/// <see cref="Supervisor.RunTag"/> wraps the run's sink in one of these.
/// </para>
/// <para>
/// Only <see cref="RunStarted"/> carries the tag, so only <see cref="RunStarted"/> is rewritten;
/// every other event passes through untouched. Like every sink, <see cref="Post"/> never blocks
/// and never throws.
/// </para>
/// </remarks>
public sealed class TagRewritingSink : IMonitorEventSink
{
    private readonly IMonitorEventSink _inner;

    public TagRewritingSink(IMonitorEventSink inner, string tag)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Tag = tag ?? throw new ArgumentNullException(nameof(tag));
    }

    /// <summary>The tag every <see cref="RunStarted"/> posted through this sink carries.</summary>
    public string Tag { get; }

    public void Post(MonitorEvent @event)
        => _inner.Post(@event is RunStarted started ? started with { Tag = Tag } : @event);
}
