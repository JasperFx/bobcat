using YamlDotNet.Serialization;

namespace Bobcat.EventModel;

public enum EventModelFileKind
{
    /// <summary>The curated format: top-level <c>schema:</c> + <c>model:</c>.</summary>
    Curated,

    /// <summary>An emlang board export: a lone top-level <c>slices:</c> map.</summary>
    Emlang,

    /// <summary>
    /// The spec-ownership manifest (issue #324 part 4): <c>schema:</c> + <c>model:</c> like the
    /// curated format, but its <c>slices:</c> entries are keyed <c>slice:</c> rather than
    /// <c>name:</c>.
    /// </summary>
    SpecOwnership,

    Unknown,
}

/// <summary>
/// Tells the three YAML shapes apart by their keys, so one <c>import-event-model</c> command takes
/// any of them and says something useful about the two it was not handed.
/// </summary>
/// <remarks>
/// <para>
/// The emlang export is easy — it has no <c>schema:</c>/<c>model:</c>. The curated model and the
/// spec-ownership manifest share both of those, and are told apart one level down: a curated slice
/// is <c>name:</c>, a manifest entry is <c>slice:</c>.
/// </para>
/// <para>
/// <b>Ambiguity resolves to Curated, deliberately.</b> An empty or mixed <c>slices:</c> list could
/// be either, and guessing Unknown there would turn a file that loads today into an error — the
/// manifest is supposed to be purely additive. So only an unambiguous manifest is reported as one,
/// and everything else keeps the answer it got before this enum grew a member.
/// </para>
/// </remarks>
public static class EventModelFileSniffer
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    public static EventModelFileKind Sniff(string yaml)
    {
        Dictionary<object, object>? root;
        try
        {
            root = Deserializer.Deserialize<Dictionary<object, object>>(yaml);
        }
        catch
        {
            return EventModelFileKind.Unknown;
        }

        if (root is null) return EventModelFileKind.Unknown;

        var keys = root.Keys.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
        if (keys.Contains("schema") || keys.Contains("model"))
        {
            return isSpecOwnership(root) ? EventModelFileKind.SpecOwnership : EventModelFileKind.Curated;
        }

        return keys.Contains("slices") ? EventModelFileKind.Emlang : EventModelFileKind.Unknown;
    }

    private static bool isSpecOwnership(Dictionary<object, object> root)
    {
        if (!root.TryGetValue("slices", out var value) || value is not List<object> entries) return false;

        var sawManifestEntry = false;
        foreach (var entry in entries)
        {
            if (entry is not Dictionary<object, object> map) return false;

            var keys = map.Keys.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
            if (keys.Contains("name")) return false;
            if (!keys.Contains("slice")) return false;

            sawManifestEntry = true;
        }

        return sawManifestEntry;
    }
}
