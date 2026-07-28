namespace Bobcat.Runtime;

/// <summary>
/// Cross-cutting setup/teardown that runs ONCE for the whole test run — seeding reference
/// data, priming a cache, installing a global fake clock.
///
/// Register explicitly on the suite: <c>suite.AddGlobalAction(new SeedReferenceData())</c>.
/// There is no discovered "system" class and no lambda registration; if the work is
/// resource-shaped (owns a connection, a container, a host), write an
/// <see cref="ITestResource"/> instead — it already has the lifecycle.
///
/// <see cref="SetUp"/> runs after every resource has started (so resources are available)
/// and before the first feature. <see cref="TearDown"/> runs after the last feature and
/// before resources are disposed, in reverse registration order.
/// </summary>
public interface IGlobalAction
{
    Task SetUp();
    Task TearDown();
}
