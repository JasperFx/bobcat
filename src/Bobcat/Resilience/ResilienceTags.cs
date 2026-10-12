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
    /// <summary>Number of attempts allowed. Trait: <c>Retry=2</c>.</summary>
    public const string Retry = "Retry";

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
