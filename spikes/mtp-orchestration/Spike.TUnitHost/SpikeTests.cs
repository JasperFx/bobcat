using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Spike.TUnitHost;

/// <summary>
/// The same outcome shapes as the xUnit host, so the spike can compare what two independently
/// written MTP frameworks actually put on the wire.
/// </summary>
public class SpikeTests
{
    [Test]
    public async Task passes() => await Assert.That(2 + 2).IsEqualTo(4);

    [Test]
    public async Task fails_an_assertion() => await Assert.That(2 + 3).IsEqualTo(4);

    [Test]
    public Task throws_a_custom_exception() => throw new BrokerUnavailableException("rabbit went away");

    [Test]
    [Skip("deliberately skipped so the spike sees a skip outcome")]
    public Task is_skipped() => Task.CompletedTask;

    /// <summary>Q4: tUnit's own metadata attribute — does it reach the parent?</summary>
    [Test]
    [Property("Isolated", "true")]
    [Property("RecycleOnRetry", "rabbit")]
    public Task carries_traits() => Task.CompletedTask;

    /// <summary>Q3: the selective re-run target.</summary>
    [Test]
    public async Task flaky_until_told_otherwise()
    {
        var flag = Environment.GetEnvironmentVariable("SPIKE_FLAKY_PASSES");
        await Assert.That(flag).IsEqualTo("true");
    }

    [Test]
    public Task kills_the_process_when_armed()
    {
        if (Environment.GetEnvironmentVariable("SPIKE_CRASH") == "true") Environment.Exit(70);
        return Task.CompletedTask;
    }

    [Test]
    public async Task sleeps_when_armed(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("SPIKE_SLOW") == "true")
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }
}

public class BrokerUnavailableException(string message) : Exception(message);
