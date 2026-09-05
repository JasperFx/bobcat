using YamlDotNet.Serialization;

namespace Bobcat.EventModel;

public enum EventModelFileKind
{
    /// <summary>The curated format: top-level <c>schema:</c> + <c>model:</c>.</summary>
    Curated,

    /// <summary>An emlang board export: a lone top-level <c>slices:</c> map.</summary>
    Emlang,

    Unknown,
}

/// <summary>
/// Tells a curated file from an emlang export by the root keys, so one <c>import-event-model</c>
/// command takes either. Both formats have a <c>slices:</c> node; only the curated one carries
/// <c>schema:</c>/<c>model:</c>.
/// </summary>
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
        if (keys.Contains("schema") || keys.Contains("model")) return EventModelFileKind.Curated;
        return keys.Contains("slices") ? EventModelFileKind.Emlang : EventModelFileKind.Unknown;
    }
}
