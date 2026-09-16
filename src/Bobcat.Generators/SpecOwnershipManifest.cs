using System;
using System.Collections.Generic;

namespace Bobcat.Generators;

/// <summary>
/// The generator's copy of the spec-ownership manifest (issue #324 part 4) — enough of it to tell
/// which lane a slice's specifications belong in, read without a YAML library.
/// </summary>
/// <remarks>
/// <para>
/// Duplicated, not shared, for the same reason as <see cref="GeneratorSliceTags"/> and
/// <see cref="CodeFirstNaming"/>: this project is netstandard2.0 and references nothing. Shipping
/// YamlDotNet inside an analyzer means the consuming build loads whichever copy Roslyn resolves
/// first, which is a version fight nobody wants from a diagnostic.
/// <c>SpecOwnershipParsingAgreementTests</c> pins the two implementations together, exactly as the
/// other two duplicated parsers are pinned.
/// </para>
/// <para>
/// <b>Deliberately narrow.</b> It understands one flat shape — top-level scalars plus a
/// <c>slices:</c> list of scalar-only entries — and it recognises only the four keys the
/// diagnostics need. Anything nested is skipped rather than guessed at, because a reader that
/// quietly half-understands a richer file is #318's failure mode in a new place.
/// </para>
/// </remarks>
internal static class SpecOwnershipManifest
{
    /// <summary>
    /// Whether an <c>AdditionalFiles</c> entry is a manifest, by name. A convention rather than a
    /// sniff: the analyzer should not be parsing every YAML file a project happens to include, and
    /// a name the author chose on purpose is a clearer contract than content matching.
    /// </summary>
    public static bool IsManifestFile(string path)
    {
        var slash = path.LastIndexOfAny(new[] { '/', '\\' });
        var name = (slash < 0 ? path : path.Substring(slash + 1)).Replace("-", "").Replace("_", "");
        return name.EndsWith("specownership.yaml", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("specownership.yml", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class Entry
    {
        public string Slice = "";
        public string? Kind;
        public string? Authoring;
        public string? Owner;

        /// <summary>
        /// <c>gherkin</c> | <c>codefirst</c> | <c>projected</c>, normalized. Unstated means
        /// <c>gherkin</c> — except under <c>kind: unit</c>, where projected is the only pairing the
        /// format permits.
        /// </summary>
        public string ResolvedAuthoring
        {
            get
            {
                var stated = Normalize(Authoring);
                if (stated == "gherkin" || stated == "codefirst" || stated == "projected") return stated;
                return Normalize(Kind) == "unit" ? "projected" : "gherkin";
            }
        }
    }

    internal sealed class Manifest
    {
        public string Model = "";
        public readonly List<Entry> Slices = new();

        public Entry? For(string slice)
        {
            foreach (var entry in Slices)
            {
                if (string.Equals(entry.Slice, slice, StringComparison.Ordinal)) return entry;
            }

            return null;
        }

        /// <summary>The lane this manifest puts a slice in — <c>gherkin</c> for one it does not list.</summary>
        public string AuthoringFor(string slice) => For(slice)?.ResolvedAuthoring ?? "gherkin";
    }

    public static string Normalize(string? value)
        => value is null ? "" : value.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant();

    public static Manifest Read(string yaml)
    {
        var manifest = new Manifest();
        Entry? current = null;
        var inSlices = false;

        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = stripComment(raw);
            if (line.Trim().Length == 0) continue;

            var indent = line.Length - line.TrimStart(' ').Length;
            var text = line.Trim();

            // A top-level key closes any list that was open.
            if (indent == 0 && !text.StartsWith("-", StringComparison.Ordinal))
            {
                current = null;
                inSlices = false;

                if (!split(text, out var key, out var value)) continue;

                if (key == "slices") inSlices = true;
                else if (key == "model") manifest.Model = value;

                continue;
            }

            if (!inSlices) continue;

            if (text.StartsWith("- ", StringComparison.Ordinal) || text == "-")
            {
                current = new Entry();
                manifest.Slices.Add(current);

                var rest = text.Length > 1 ? text.Substring(1).Trim() : "";
                if (rest.Length > 0) apply(current, rest);
                continue;
            }

            if (current is not null) apply(current, text);
        }

        manifest.Slices.RemoveAll(x => x.Slice.Length == 0);
        return manifest;
    }

    private static void apply(Entry entry, string text)
    {
        if (!split(text, out var key, out var value)) return;

        switch (key)
        {
            case "slice": entry.Slice = value; break;
            case "kind": entry.Kind = value; break;
            case "authoring": entry.Authoring = value; break;
            case "owner": entry.Owner = value; break;
        }
    }

    private static bool split(string text, out string key, out string value)
    {
        key = "";
        value = "";

        var colon = text.IndexOf(':');
        if (colon <= 0) return false;

        key = text.Substring(0, colon).Trim().ToLowerInvariant();
        value = unquote(text.Substring(colon + 1).Trim());
        return key.Length > 0;
    }

    /// <summary>
    /// A trailing <c>#</c> comment, but only where YAML would treat it as one — preceded by
    /// whitespace and outside quotes. A naive split on <c>#</c> would truncate
    /// <c>coveredBy: Sprint #3/…</c>, which is the kind of value this file really carries.
    /// </summary>
    private static string stripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            if (c == '"' || c == '\'') { quote = c; continue; }
            if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line.Substring(0, i);
        }

        return line;
    }

    private static string unquote(string value)
    {
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
        {
            return value.Substring(1, value.Length - 2);
        }

        return value;
    }
}
