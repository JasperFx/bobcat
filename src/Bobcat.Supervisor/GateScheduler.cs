using System.Runtime.ExceptionServices;

namespace Bobcat.Supervisor;

/// <summary>
/// The gate's list scheduler (bobcat#729): most work first, a unit starts when every
/// track it names is free and its workers fit in what is left of the budget, and whenever a unit
/// finishes the first waiting unit that now fits starts. Pure over <see cref="GateUnit"/>s and a
/// delegate, so the tracks, the budget and the ordering are tested without a test host.
/// </summary>
internal static class GateScheduler
{
    /// <summary>
    /// The order units are offered to the budget: units with no prediction first, in declaration
    /// order, then the rest by their predicted work, most first: the predicted wall clock times the
    /// workers the unit runs on under <paramref name="budget"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An unknown unit goes first rather than being charged the median, because the two mistakes
    /// are not symmetric: a short unknown started early costs nothing, a long unknown started
    /// last is the whole makespan. After one run every unit is known.
    /// </para>
    /// <para>
    /// Work, not wall clock: a unit on four lanes predicted at four minutes is sixteen worker-minutes,
    /// and it can only start when four workers are free at once. Ordered by wall clock it sorted
    /// behind every single-worker unit of five minutes or more, and once those were running each
    /// worker that came free went to the next single-worker unit, so it started when the gate was
    /// nearly empty and ran on as the tail (Wolverine's LocalGate: three such units started 15
    /// minutes in and ended a 2h17m-of-work, 9-worker gate at 21m48s). Ordered by work, the units
    /// that need several workers at once claim them while the whole budget is still free.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<GateUnit> Order(IReadOnlyList<GateUnit> units, Func<GateUnit, TimeSpan?> predicted,
        int budget = int.MaxValue)
    {
        var indexed = units.Select((unit, index) => (unit, index, predicted: predicted(unit))).ToArray();

        return indexed
            .OrderBy(x => x.predicted is null ? 0 : 1)
            .ThenByDescending(x => (x.predicted ?? TimeSpan.Zero) * WorkersFor(x.unit, budget))
            .ThenBy(x => x.index)
            .Select(x => x.unit)
            .ToArray();
    }

    /// <summary>The workers a unit actually gets under <paramref name="budget"/>: clamped, never held back forever.</summary>
    public static int WorkersFor(GateUnit unit, int budget) => Math.Clamp(unit.Workers, 1, Math.Max(1, budget));

    /// <summary>
    /// Run every unit of <paramref name="ordered"/> through <paramref name="run"/> under the
    /// track and budget rules. <paramref name="run"/> receives the unit and the workers it may
    /// use. A delegate that throws stops new units from starting; the throw is rethrown once every
    /// unit already running has returned, so the gate never returns with hosts still alive.
    /// Cancellation is the same path.
    /// </summary>
    public static async Task Run(
        IReadOnlyList<GateUnit> ordered,
        int budget,
        Func<GateUnit, int, CancellationToken, Task> run,
        CancellationToken ct = default)
    {
        budget = Math.Max(1, budget);

        var waiting = new List<GateUnit>(ordered);
        var running = new Dictionary<Task, (GateUnit Unit, int Workers)>();
        var heldTracks = new HashSet<Track>();
        var workersInUse = 0;
        ExceptionDispatchInfo? fault = null;

        while (waiting.Count > 0 || running.Count > 0)
        {
            if (fault is null && !ct.IsCancellationRequested)
            {
                // Scan in plan order and start everything that fits. A unit that does not fit is
                // stepped over, not waited on: a short no-track unit should fill the gap beside a
                // long one holding the only track, which is the whole point of tracks.
                for (var i = 0; i < waiting.Count;)
                {
                    var unit = waiting[i];
                    var workers = WorkersFor(unit, budget);

                    if (workers <= budget - workersInUse && unit.Tracks.All(t => !heldTracks.Contains(t)))
                    {
                        waiting.RemoveAt(i);
                        foreach (var track in unit.Tracks) heldTracks.Add(track);
                        workersInUse += workers;
                        running[run(unit, workers, ct)] = (unit, workers);
                        continue;
                    }

                    i++;
                }
            }

            if (running.Count == 0) break;

            var finished = await Task.WhenAny(running.Keys);
            var (done, released) = running[finished];
            running.Remove(finished);
            foreach (var track in done.Tracks) heldTracks.Remove(track);
            workersInUse -= released;

            try
            {
                await finished;
            }
            catch (Exception e)
            {
                // Kept, not thrown: the units still running finish first, so a gate never returns
                // with hosts alive behind it. The first fault is the one reported.
                fault ??= ExceptionDispatchInfo.Capture(e);
            }
        }

        fault?.Throw();
        if (waiting.Count > 0) ct.ThrowIfCancellationRequested();
    }
}
