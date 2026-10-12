// Inert unless armed. When it is, no test in this process can run, and the supervisor must hear
// that as a reported failure of every test rather than as a crash — issue #123's shape, in the
// form a projected suite has it: a shared resource behind an xUnit assembly fixture.
[assembly: AssemblyFixture(typeof(Bobcat.Supervisor.ProjectedWorker.BrokerThatWillNotStart))]

namespace Bobcat.Supervisor.ProjectedWorker;

/// <summary>
/// Throws from <see cref="InitializeAsync"/> when <c>BOBCAT_START_FAILS</c> is set — the broker
/// that is down this morning.
/// </summary>
public sealed class BrokerThatWillNotStart : IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        if (Probe.IsArmed("BOBCAT_START_FAILS"))
        {
            throw new InvalidOperationException("the broker refused the connection");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
