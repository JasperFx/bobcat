using Bobcat.Ui.Runs;
using Shouldly;

namespace Bobcat.Ui.Tests;

/// <summary>
/// bobcat#786: the board states what the console keeps ("keeps 5 runs per suite · 14 days"), and
/// the only honest source of those numbers is the registry's own configuration — a default written
/// into the page would be wrong for every console with the variables set.
/// </summary>
public class RetentionEndpointTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-retention-endpoint-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    [Fact]
    public void reports_the_configured_bounds()
    {
        using var registry = new MonitorRunRegistry(_dataPath, TimeSpan.FromDays(7), retainedRuns: 3);

        RunEndpoints.Retention(registry).ShouldBe(new RetentionPolicy(3, 7));
    }

    [Fact]
    public void a_bound_turned_off_reads_as_zero_rather_than_a_negative_number()
    {
        // Zero or a negative number turns either knob off; the page should read both the same way.
        using var registry = new MonitorRunRegistry(_dataPath, TimeSpan.FromDays(-1), retainedRuns: -5);

        RunEndpoints.Retention(registry).ShouldBe(new RetentionPolicy(0, 0));
    }
}
