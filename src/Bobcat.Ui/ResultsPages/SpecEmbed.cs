using System.Text;

namespace Bobcat.Ui.ResultsPages;

/// <summary>
/// The <c>&lt;bobcat-spec&gt;</c> web component (bobcat#553): one script a documentation page
/// includes to render a specification from a results file, through the same Vue component the
/// board and the HTML results page render with.
/// </summary>
/// <remarks>
/// Built by <c>vite.config.embed.ts</c> into <c>dist-embed/bobcat-spec.js</c> — one file, its styles
/// inside it, no Element Plus, no router — and embedded here under <c>EmbedFrontend=true</c> exactly
/// as the results page template is. <c>bobcat results-docs</c> writes it beside the Markdown it
/// generates, so a VitePress or Docusaurus page needs one script tag and one element.
/// </remarks>
public static class SpecEmbed
{
    /// <summary>The script's manifest resource name, set by Bobcat.Ui.csproj.</summary>
    public const string ResourceName = "Bobcat.Ui.bobcat-spec.js";

    /// <summary>The file name the script is written under.</summary>
    public const string FileName = "bobcat-spec.js";

    /// <summary>The element the script defines.</summary>
    public const string ElementName = "bobcat-spec";

    /// <summary>Why there is no script, said once so every caller agrees.</summary>
    public const string NotEmbeddedMessage =
        "This bobcat was built without its web pages, so it cannot write bobcat-spec.js (the <bobcat-spec> web component). "
        + "The packed tool always has it; a build from source needs `-p:EmbedFrontend=true` (and Node).";

    /// <summary>The embedded script, or null in a build without <c>EmbedFrontend</c>.</summary>
    public static string? Script()
    {
        using var stream = typeof(SpecEmbed).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
