namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// <c>SpecAssert.EventuallyAsync</c> inside a step helper, the <c>docs/marker-steps.md</c> example of
/// waiting for an answer that arrives later: a work queue drained in the background, so the outstanding
/// count reaches its expected value a few polls after the step starts asking.
/// </summary>
public class QueueSteps
{
    private readonly WorkQueue _queue = new();

    [Given("{count} items are enqueued")]
    internal void ItemsAreEnqueued(int count) => _queue.Enqueue(count);

    [When("the workers start")]
    internal void TheWorkersStart() => _queue.StartDraining();

    // #region eventually
    [Then("the outstanding count becomes {count}")]
    internal Task OutstandingBecomes(int count)
        => SpecAssert.EventuallyAsync("count", () => _queue.Outstanding, count,
            timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(50));
    // #endregion eventually

    private sealed class WorkQueue
    {
        private int _outstanding;

        public int Outstanding => Volatile.Read(ref _outstanding);

        public void Enqueue(int count) => Interlocked.Add(ref _outstanding, count);

        public void StartDraining() => _ = Task.Run(async () =>
        {
            while (Outstanding > 0)
            {
                await Task.Delay(20);
                Interlocked.Decrement(ref _outstanding);
            }
        });
    }
}
