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
/// <see cref="MarkerSpecNaming"/>: this project is netstandard2.0 and references nothing. Shipping
/// YamlDotNet inside an analyzer means the consuming build loads whichever copy Roslyn resolves
/// first, which is a version fight nobody wants from a diagnostic.
/// <c>SpecOwnershipParsingAgreementTests</c> pins the two implementations together, exactly as the
/// other two duplicated parsers are pinned.
/// </para>
/// <para>
/// <b>Deliberately narrow.</b> It understands top-level scalars, a <c>defaults:</c> block of
/// scalars, and a <c>slices:</c> list of scalar-only entries — and it recognises only the keys the
/// diagnostics need. Anything else nested is skipped rather than guessed at, because a reader that
/// quietly half-understands a richer file is #318's failure mode in a new place.
/// </para>
/// <para>
/// <b><c>defaults:</c> is not optional for this reader (issue #334).</b> Skipping it would have
/// been the narrow choice, and it would have been wrong in precisely the file the block exists
/// for: with <c>defaults: { authoring: projected }</c> and no entry per slice, a reader that sees
/// only <c>slices:</c> calls every slice gherkin — so BOBCAT025 would report a lane disagreement
/// against every projected test in the repo. <c>scaffold:</c> IS skipped, because nothing here
/// acts on it: whether the scaffolder writes a file is not a question about the compilation.
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

        /// <summary>The authoring style this ENTRY states, normalized, or "" when it says nothing.</summary>
        /// <remarks>
        /// Stated rather than resolved, mirroring <c>SpecOwnership.StatedAuthoring</c>: the answer
        /// that matters needs the file's <c>defaults:</c>, so it lives on
        /// <see cref="Manifest.AuthoringFor"/> and cannot be asked here by accident.
        /// </remarks>
        public string StatedAuthoring
        {
            get
            {
                var stated = Normalize(Authoring);
                return stated == "gherkin" || stated == "projected" ? stated : "";
            }
        }

        /// <summary>The kind this entry states, normalized, or "" when it says nothing.</summary>
        public string StatedKind
        {
            get
            {
                var stated = Normalize(Kind);
                return stated == "unit" || stated == "integration" ? stated : "";
            }
        }
    }

    /// <summary>The <c>defaults:</c> block (issue #334) — the keys this reader acts on.</summary>
    internal sealed class Defaults
    {
        public string? Kind;
        public string? Authoring;
        public string? Owner;
    }

    internal sealed class Manifest
    {
        public string Model = "";
        public Defaults? Defaults;
        public readonly List<Entry> Slices = new();

        public Entry? For(string slice)
        {
            foreach (var entry in Slices)
            {
                if (string.Equals(entry.Slice, slice, StringComparison.Ordinal)) return entry;
            }

            return null;
        }

        /// <summary>
        /// The lane this manifest puts a slice in — entry over defaults over <c>gherkin</c>, which
        /// is still what a slice gets from a manifest that says nothing (issue #334).
        /// </summary>
        public string AuthoringFor(string slice)
        {
            var entry = For(slice);

            var stated = entry?.StatedAuthoring ?? "";
            if (stated.Length > 0) return stated;

            // An entry's own `kind: unit` implies projected whatever the defaults say — unit is the
            // one kind the other styles cannot express. Same precedence as SpecOwnershipFile.Resolve.
            if (entry?.StatedKind == "unit") return "projected";

            var inherited = Normalize(Defaults?.Authoring);
            if (inherited == "gherkin" || inherited == "projected") return inherited;

            return Normalize(Defaults?.Kind) == "unit" ? "projected" : "gherkin";
        }

        /// <summary>
        /// The owner to NAME in a message, or null. A <c>defaults.owner:</c> template is not
        /// expanded: <c>{feature}</c> comes from the curated model, which this reader never sees,
        /// and an unexpanded template in a diagnostic is worse than saying nothing.
        /// </summary>
        public string? OwnerFor(string slice)
        {
            var owner = For(slice)?.Owner;
            if (owner is { Length: > 0 }) return owner;

            var template = Defaults?.Owner;
            return template is { Length: > 0 } && template.IndexOf('{') < 0 ? template : null;
        }
    }

    public static string Normalize(string? value)
        => value is null ? "" : value.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant();

    public static Manifest Read(string yaml)
    {
        var manifest = new Manifest();
        Entry? current = null;
        var inSlices = false;
        var inDefaults = false;

        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = stripComment(raw);
            if (line.Trim().Length == 0) continue;

            var indent = line.Length - line.TrimStart(' ').Length;
            var text = line.Trim();

            // A top-level key closes any block that was open.
            if (indent == 0 && !text.StartsWith("-", StringComparison.Ordinal))
            {
                current = null;
                inSlices = false;
                inDefaults = false;

                if (!split(text, out var key, out var value)) continue;

                if (key == "slices") inSlices = true;
                else if (key == "defaults") { inDefaults = true; manifest.Defaults = new Defaults(); }
                else if (key == "model") manifest.Model = value;

                continue;
            }

            if (inDefaults)
            {
                applyDefault(manifest.Defaults!, text);
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

    private static void applyDefault(Defaults defaults, string text)
    {
        if (!split(text, out var key, out var value)) return;

        switch (key)
        {
            case "kind": defaults.Kind = value; break;
            case "authoring": defaults.Authoring = value; break;
            case "owner": defaults.Owner = value; break;

            // `scaffold:` is read by the tool, not here — see the class remarks.
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
