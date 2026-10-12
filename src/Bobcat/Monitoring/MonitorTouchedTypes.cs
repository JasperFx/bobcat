namespace Bobcat.Monitoring;

/// <summary>
/// The touched types of a scenario as <c>scenario_finished</c> carries them (issues #107 and #465) — one
/// mapping for both lanes, so the projected lane's <see cref="ScenarioRecorder.Recording.TouchedTypes"/> and
/// the Gherkin lane's <c>ExecutionResults.TouchedTypes</c> reach the wire in exactly the same shape.
/// </summary>
internal static class MonitorTouchedTypes
{
    /// <summary>
    /// Each type as a wire <see cref="TouchedType"/>, in order. An empty list travels as null: nothing
    /// recorded is absence of evidence, not evidence of nothing.
    /// </summary>
    public static IReadOnlyList<TouchedType>? From(IReadOnlyList<Type> types)
        => types.Count == 0
            ? null
            : types
                .Select(t => new TouchedType(t.Name, t.FullName ?? t.Name, t.Assembly.GetName().Name ?? string.Empty))
                .ToArray();
}
