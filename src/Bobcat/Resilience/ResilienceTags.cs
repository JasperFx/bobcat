namespace Bobcat.Resilience;

/// <summary>
/// The retry vocabulary: the trait keys a <see cref="IFailurePolicy"/> reads off
/// <see cref="AttemptContext.Traits"/>.
/// </summary>
/// <remarks>
/// These names are engine-level, not framework-level. An xUnit test writes
/// <c>[Trait("Isolated", "true")]</c>, a TUnit test <c>[Property("Isolated", "true")]</c>; both
/// reach the supervisor as the same trait over the MTP wire, which is what lets one policy serve
/// every runner.
/// </remarks>
public static class ResilienceTags
{
    /// <summary>
    /// Number of attempts allowed. Trait: <c>Retry=2</c>. Each retry runs alone in a fresh process
    /// unless the test also carries <see cref="RetryWarm"/> (bobcat#661).
    /// </summary>
    public const string Retry = "Retry";

    /// <summary>
    /// Retry in the same, warm worker process rather than a fresh one. Trait: <c>RetryWarm=true</c>.
    /// </summary>
    /// <remarks>
    /// The opt-in, not the default (bobcat#661). A failure that pollutes its process — a handler
    /// registry, a static, a half-built schema — fails every warm retry with leftovers of the
    /// first attempt (<c>design/wolverine-ci-rollout.md</c> lesson 5), and an xUnit worker has no
    /// reset bracket, so the fresh process IS the bracket. Wolverine, Polecat and Marten each wrote
    /// their own fresh-process policy to get that; warm is for a test known to leave nothing
    /// behind, where a launch is the larger cost.
    /// </remarks>
    public const string RetryWarm = "RetryWarm";

    /// <summary>Only reliable when run alone. Trait: <c>Isolated=true</c>.</summary>
    public const string Isolated = "Isolated";

    /// <summary>
    /// Resources to recycle before retrying, comma-separated. Trait: <c>RecycleOnRetry=rabbit</c>.
    /// </summary>
    public const string RecycleOnRetry = "RecycleOnRetry";

    /// <summary>Resource names from a <c>RecycleOnRetry</c> trait value.</summary>
    public static string[] ParseResources(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
