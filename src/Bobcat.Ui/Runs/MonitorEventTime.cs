using Bobcat.Ui.Contracts;

namespace Bobcat.Ui.Runs;

/// <summary>
/// The publisher's own stamp on an event, when it carries one (bobcat#784): what an archive replay
/// uses for <see cref="RunProjection.LastEventAt"/>, so a rehydrated run reads as last heard from
/// when its publisher last spoke rather than when the console restarted. A step event carries no
/// stamp of its own and answers null, which leaves the previous stamp standing.
/// </summary>
public static class MonitorEventTime
{
    public static DateTimeOffset? Of(MonitorEvent @event) => @event switch
    {
        RunStarted e => e.StartedAt,
        RunHeartbeat e => e.At,
        RunFinished e => e.FinishedAt,
        ScenarioStarted e => e.At,
        ScenarioFinished e => e.At,
        LaneStarted e => e.At,
        LaneFinished e => e.At,
        ResourceRecycled e => e.At,
        WorkerFaulted e => e.At,
        WorkerStarted e => e.At,
        TestStalled e => e.At,
        TestStarted e => e.At,
        TestFinished e => e.At,
        RunProgress e => e.At,
        _ => null
    };
}
