
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

    /// <summary>An eventmodelers.ai board backup: JSON with <c>nodes</c> and <c>metadata</c> (bobcat#424).</summary>
    EventModelersBoard,

    /// <summary>An eventmodelers.ai <c>config.json</c> / slice export: JSON with <c>slices[]</c> (bobcat#424).</summary>
    EventModelersConfig,

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

    public static EventModelFileKind Sniff(string yaml)
    {
        // JSON is also YAML, so the platform's JSON exports are told apart first: a config.json's
        // top-level slices[] would otherwise read as an emlang file and fail on its shape
        if (Emlang.EventModelersJsonReader.LooksLikeJson(yaml))
            return Emlang.EventModelersJsonReader.Sniff(yaml) ?? EventModelFileKind.Unknown;

        List<Dictionary<object, object>> documents;
        try
        {
            // An emlang file may hold several documents separated by `---`, often behind a
            // comment-only preamble (issue #422), so every document is looked at, not the first
            documents = Emlang.EmlangReader.Documents(yaml);
        }
        catch
        {
            return EventModelFileKind.Unknown;
        }

        foreach (var root in documents)
        {
            var keys = root.Keys.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
            if (keys.Contains("schema") || keys.Contains("model"))
            {
                return isSpecOwnership(root) ? EventModelFileKind.SpecOwnership : EventModelFileKind.Curated;
            }
        }

        return documents.Any(x => x.ContainsKey("slices")) ? EventModelFileKind.Emlang : EventModelFileKind.Unknown;
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
