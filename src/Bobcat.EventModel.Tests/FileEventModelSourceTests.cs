using Bobcat.EventModel;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace Bobcat.EventModel.Tests;

public class FileEventModelSourceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bobcat-event-model-file-{Guid.NewGuid():N}");

    public FileEventModelSourceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch
        {
            // Best effort; temp cleanup.
        }
    }

    private string write(string yaml)
    {
        var path = Path.Combine(_directory, "model.emodel.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public async Task a_missing_file_yields_null_because_registration_may_precede_the_file()
    {
        var source = new FileEventModelSource(Path.Combine(_directory, "absent.yaml"));

        (await source.TryCreateAsync(null!, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task a_valid_file_loads_as_a_descriptor()
    {
        var source = new FileEventModelSource(write(
            """
            schema: 1
            model: CritterCrush
            slices:
              - name: SwipeOnDog
                pattern: Command
                events: [DogLiked]
            """));

        var descriptor = await source.TryCreateAsync(null!, CancellationToken.None);

        descriptor!.Name.ShouldBe("CritterCrush");
        descriptor.Slices.Single().Pattern.ShouldBe(SlicePattern.Command);
    }

    [Fact]
    public async Task an_invalid_file_fails_loudly_naming_every_problem()
    {
        var source = new FileEventModelSource(write("slices: []"));

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => source.TryCreateAsync(null!, CancellationToken.None));

        failure.Message.ShouldContain("schema must be 1");
        failure.Message.ShouldContain("merge key");
    }

    [Fact]
    public void the_source_sits_on_the_declared_rung_by_default()
    {
        // Provenance is a default interface member — only reachable through the interface, and
        // Declared is exactly what a file that predates any code should claim.
        IEventModelDefinitionSource source = new FileEventModelSource("whatever.yaml");

        source.Provenance.ShouldBe(EventModelProvenance.Declared);
        source.Subject.ShouldBe(new Uri("event-model://file/whatever.yaml"));
    }
}
