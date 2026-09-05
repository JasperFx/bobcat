using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bobcat.EventModel;

/// <summary>
/// Serializes a <see cref="CuratedModelFile"/> back to curated YAML — the emlang importer's
/// output path (issue #202): the import writes a reviewable, correctable curated file rather
/// than a descriptor, so segmentation guesses are a diff away from being fixed.
/// </summary>
public static class CuratedModelWriter
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    public static string Write(CuratedModelFile file) => Serializer.Serialize(file);
}
