namespace Bobcat.Tests.Resilience;

/// <summary>
/// Builds the trait dictionary an <see cref="Bobcat.Resilience.AttemptContext"/> carries from
/// <c>Key=Value</c> pairs, the shape an xUnit <c>[Trait("Retry", "2")]</c> arrives in over the MTP
/// wire. Keys are matched case-insensitively, as they are on the real path.
/// </summary>
internal static class TestTraits
{
    public static IReadOnlyDictionary<string, string> Of(params string[] traits)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trait in traits)
        {
            var parts = trait.Split('=', 2);
            dictionary[parts[0]] = parts.Length > 1 ? parts[1] : "true";
        }

        return dictionary;
    }
}
