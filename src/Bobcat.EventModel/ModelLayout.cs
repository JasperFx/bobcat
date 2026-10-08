namespace Bobcat.EventModel;

/// <summary>One file a writer produced: a path relative to the directory it is written under, and its content.</summary>
/// <param name="Path">Relative, with <c>/</c> separators, e.g. <c>Features/Booking/ConfirmAppointment.cs</c>.</param>
/// <param name="Content">The file's text.</param>
public sealed record GeneratedFile(string Path, string Content);

/// <summary>
/// Where every type an imported model names lives: which file, and which namespace (bobcat#441).
/// Shared by <see cref="CSharpModelWriter"/>, which writes the types there, and
/// <c>EmlangSpecWriter</c>, which has to <c>using</c> exactly the namespaces they ended up in.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file per slice, in a folder per chapter.</b> A slice's file holds what the slice
/// produces: its command (or the read model of a view), the events it emits and the messages it
/// publishes. That is the file <c>wolverine scaffold</c> adds the handler to, so a command and its
/// handler end up side by side. The first slice that produces a type owns it.
/// </para>
/// <para>
/// <b>A type no slice produces gets a file of its own</b> under the chapter of the first slice that
/// names it: aggregates, events consumed from elsewhere, what a slice reads. A type only a
/// specification names (an example from a test no slice claims) goes under <c>Features/</c> in the
/// root namespace.
/// </para>
/// <para>
/// <b>The chapter is the namespace.</b> <c>{Namespace}.{Chapter}</c>, the folder name sanitized the
/// same way. A slice without a chapter stays in the root namespace.
/// </para>
/// </remarks>
public sealed class ModelLayout
{
    /// <summary>The folder every type file lives under, relative to the output directory.</summary>
    public const string FeaturesFolder = "Features";

    private readonly Dictionary<string, (string? Chapter, string File)> _types = new(StringComparer.Ordinal);

    private ModelLayout(string rootNamespace)
    {
        RootNamespace = rootNamespace;
    }

    /// <summary>The namespace the definition lives in, and the root of every chapter's.</summary>
    public string RootNamespace { get; }

    /// <summary>Lay out <paramref name="model"/>, plus the types only specifications name.</summary>
    /// <param name="extra">Types the specs need that no slice names (streams, examples' elements).</param>
    public static ModelLayout For(ImportedEventModel model, string rootNamespace, IEnumerable<string>? extra = null)
    {
        var layout = new ModelLayout(rootNamespace);

        // Produced: the slice's own file
        foreach (var slice in model.Slices)
        {
            var chapter = ChapterFolder(slice.Chapter);
            var file = sliceFile(chapter, slice.Name);

            var produced = new List<string?>();
            if (slice.Pattern?.Equals("View", StringComparison.OrdinalIgnoreCase) == true)
            {
                produced.AddRange(slice.ReadModels);
                produced.Add(slice.Command);
            }
            else
            {
                produced.Add(slice.Command);
                produced.AddRange(slice.ReadModels);
            }

            produced.AddRange(slice.Events);
            produced.AddRange(slice.Messages);

            foreach (var name in produced) layout.place(name, chapter, file);
        }

        // Named but not produced: a file of its own, beside the first slice that names it
        foreach (var slice in model.Slices)
        {
            var chapter = ChapterFolder(slice.Chapter);
            foreach (var name in slice.Aggregates.Concat(slice.ConsumedEvents).Concat(slice.ReadsFrom))
            {
                var type = CSharpModelWriter.Identifiers.Sanitize(name ?? "");
                if (type.Length > 0) layout.place(type, chapter, typeFile(chapter, type));
            }
        }

        foreach (var name in extra ?? [])
        {
            var type = CSharpModelWriter.Identifiers.Sanitize(name ?? "");
            if (type.Length > 0) layout.place(type, null, typeFile(null, type));
        }

        return layout;
    }

    private void place(string? name, string? chapter, string file)
    {
        var type = CSharpModelWriter.Identifiers.Sanitize(name ?? "");
        if (type.Length == 0 || _types.ContainsKey(type)) return;
        _types[type] = (chapter, file);
    }

    /// <summary>Every type laid out, in the order it was placed.</summary>
    public IReadOnlyCollection<string> Types => _types.Keys;

    /// <summary>The file a type is written to, relative to the output directory.</summary>
    public string FileOf(string type) => _types.TryGetValue(type, out var placed) ? placed.File : typeFile(null, type);

    /// <summary>The chapter folder a type lives under, or null for the root namespace.</summary>
    public string? ChapterOf(string type) => _types.GetValueOrDefault(type).Chapter;

    /// <summary>The namespace a type is declared in.</summary>
    public string NamespaceOf(string type) => NamespaceFor(RootNamespace, ChapterOf(type));

    /// <summary>Every namespace a type was put in, root first.</summary>
    public IReadOnlyList<string> Namespaces
        => _types.Values.Select(x => NamespaceFor(RootNamespace, x.Chapter))
            .Prepend(RootNamespace)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary><c>{root}.{chapter}</c>, or the root itself when there is no chapter.</summary>
    public static string NamespaceFor(string root, string? chapter)
        => chapter is { Length: > 0 } ? $"{root}.{chapter}" : root;

    /// <summary>A chapter's folder and namespace segment: the chapter name as an identifier, or null.</summary>
    public static string? ChapterFolder(string? chapter)
    {
        if (string.IsNullOrWhiteSpace(chapter)) return null;
        var name = CSharpModelWriter.Identifiers.Sanitize(Emlang.EmlangImport.PascalName(chapter));
        return name.Length == 0 ? null : name;
    }

    private static string sliceFile(string? chapter, string slice)
    {
        var name = CSharpModelWriter.Identifiers.Sanitize(slice);
        return typeFile(chapter, name.Length == 0 ? "Unnamed" : name);
    }

    private static string typeFile(string? chapter, string type)
        => chapter is null ? $"{FeaturesFolder}/{type}.cs" : $"{FeaturesFolder}/{chapter}/{type}.cs";
}
