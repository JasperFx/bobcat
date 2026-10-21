using Shouldly;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The <c>docs/xunit.md</c> wiring example: <c>[BobcatFeature]</c> and nothing else, recorded by the
/// assembly-level hook the package applies (declared in this project's csproj as the package's
/// props would). Marker comments are its steps. Green.
/// </summary>
// #region feature-only
[BobcatFeature("Async daemon")]
public class DaemonSpecs
{
    private readonly List<int> _published = new();
    private int _projected;

    [Fact]
    public async Task the_daemon_catches_up()
    {
        // Given events are published
        await PublishEvents(10);

        // When the daemon runs
        await RunDaemon();

        // Then the projection is caught up
        _projected.ShouldBe(10);
    }

    private Task PublishEvents(int count)
    {
        _published.AddRange(Enumerable.Range(1, count));
        return Task.CompletedTask;
    }

    private Task RunDaemon()
    {
        _projected = _published.Count;
        return Task.CompletedTask;
    }
}
// #endregion feature-only
