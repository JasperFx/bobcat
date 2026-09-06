using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;

namespace Bobcat.EventModel;

/// <summary>
/// An <see cref="IEventModelDefinitionSource"/> over a curated event-model YAML file (issue
/// #201) — the fourth kind of source, and the one a non-developer can author. Sits on the
/// Declared rung (the interface's default), so once code exists, the Derived and Observed rungs
/// win each role and any dropped declared claim surfaces as a disagreement hotspot.
/// </summary>
/// <remarks>
/// A missing file yields a null descriptor — the registration can predate the file in a
/// spec-first flow. An invalid file throws instead: a file the host explicitly registered is
/// meant to load, and failing quietly would draw a blank canvas with no explanation.
/// </remarks>
public sealed class FileEventModelSource : IEventModelDefinitionSource
{
    private readonly string _path;

    public FileEventModelSource(string path)
    {
        _path = path;
    }

    public Uri Subject => new($"event-model://file/{Path.GetFileName(_path)}");

    public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
    {
        if (!File.Exists(_path)) return Task.FromResult<EventModelDescriptor?>(null);

        var reading = CuratedModelReader.Read(File.ReadAllText(_path));
        if (!reading.Succeeded)
        {
            throw new InvalidOperationException(
                $"The event-model file '{_path}' did not load:{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", reading.Problems)}");
        }

        return Task.FromResult<EventModelDescriptor?>(CuratedModelMapper.ToDescriptor(reading.File!));
    }
}

public static class FileEventModelSourceExtensions
{
    /// <summary>
    /// Register a curated event-model YAML file as a Declared-rung source, folded into the
    /// assembled model like any other <see cref="IEventModelDefinitionSource"/>.
    /// </summary>
    public static IServiceCollection AddEventModelFile(this IServiceCollection services, string path)
        => services.AddEventModelSource(new FileEventModelSource(path));
}
