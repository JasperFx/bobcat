using Xunit;

namespace Spike.XunitV3Host;

/// <summary>
/// Deliberately shaped to answer the spike's questions, not to test anything:
/// one of every outcome the supervisor would need to classify.
/// </summary>
public class SpikeTests
{
    [Fact]
    public void passes() => Assert.True(true);

    [Fact]
    public void passes_too() => Assert.Equal(4, 2 + 2);

    /// <summary>Q5: an ordinary assertion failure — the "FailAndContinue" shape.</summary>
    [Fact]
    public void fails_an_assertion() => Assert.Equal(4, 2 + 3);

    /// <summary>Q5: a distinct exception type — the policy engine keys off this.</summary>
    [Fact]
    public void throws_a_custom_exception() => throw new BrokerUnavailableException("rabbit went away");

    [Fact(Skip = "deliberately skipped so the spike sees a skip outcome")]
    public void is_skipped() { }

    /// <summary>Q4: does a trait reach the parent through MTP metadata?</summary>
    [Fact]
    [Trait("Isolated", "true")]
    [Trait("RecycleOnRetry", "rabbit")]
    public void carries_traits() { }

    /// <summary>
    /// Q3: the retry target. Fails until the attempt counter file says otherwise, so the spike
    /// can prove a selective re-run actually re-executes just this test.
    /// </summary>
    [Fact]
    public void flaky_until_told_otherwise()
    {
        var flag = Environment.GetEnvironmentVariable("SPIKE_FLAKY_PASSES");
        Assert.True(flag == "true", "flaky test failing (set SPIKE_FLAKY_PASSES=true to pass)");
    }

    /// <summary>
    /// Q5: process death mid-run. Only arms when the parent explicitly asks, so a normal run
    /// of this project does not kill itself.
    /// </summary>
    [Fact]
    public void kills_the_process_when_armed()
    {
        if (Environment.GetEnvironmentVariable("SPIKE_CRASH") == "true")
        {
            Environment.Exit(70);
        }
    }

    /// <summary>Q6: something slow enough to cancel.</summary>
    [Fact]
    public async Task sleeps_when_armed()
    {
        if (Environment.GetEnvironmentVariable("SPIKE_SLOW") == "true")
        {
            await Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }
}

public class BrokerUnavailableException(string message) : Exception(message);
