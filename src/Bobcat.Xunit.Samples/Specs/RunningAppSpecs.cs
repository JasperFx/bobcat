using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The <c>docs/marker-steps.md</c> environment-lost example (bobcat#539): a fixture that starts the
/// application throws <see cref="SpecCatastrophicException"/> when it cannot, so every scenario after it
/// fails at once instead of each waiting out its own timeout. Here the app starts, so the spec is green;
/// <c>Bobcat.Xunit.Tests/EnvironmentLossTests</c> proves the halt against a fixture that throws.
/// </summary>
[BobcatFeature("A running app"), BobcatScenario]
public class RunningAppSpecs(RunningApp app) : IClassFixture<RunningApp>
{
    [Fact]
    public void the_app_answers()
    {
        // Then the app is up
        app.IsUp.ShouldBeTrue();
    }
}

// #region catastrophic-fixture
public sealed class RunningApp : IAsyncLifetime
{
    private readonly SampleWebApp _app = new();

    public bool IsUp => _app.IsHealthy;

    public async ValueTask InitializeAsync()
    {
        try
        {
            await _app.StartAsync();
            await _app.WaitUntilHealthyAsync();
        }
        catch (Exception e)
        {
            throw new SpecCatastrophicException($"the app did not start: {e.Message}", e);
        }
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
// #endregion catastrophic-fixture

/// <summary>A stand-in for the application under test, which starts in a few milliseconds.</summary>
public sealed class SampleWebApp : IAsyncDisposable
{
    public bool IsHealthy { get; private set; }

    public Task StartAsync() => Task.Delay(5);

    public Task WaitUntilHealthyAsync()
    {
        IsHealthy = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsHealthy = false;
        return ValueTask.CompletedTask;
    }
}
