using Bobcat;
using Bobcat.Alba;
using Bobcat.CritterStack;
using Bobcat.Runtime;

namespace CleanArchitectureTodos.Tests;

/// <summary>
/// Suite configuration for this spec project. No <c>Main</c>: the project references
/// <c>Bobcat.Mtp</c> and declares no entry point, so the generator emits the
/// Microsoft.Testing.Platform one and calls every <c>[BobcatConfiguration]</c> method.
/// Declaring none is also what keeps <c>AlbaResource&lt;Program&gt;</c> unambiguous — see
/// docs/resources.md.
/// </summary>
public static class SpecsRunner
{
    [BobcatConfiguration]
    public static void Configure(BobcatRunner runner)
    {
        // The reset hook is load-bearing here, the same way it is in OutboxDemo. Todo list
        // titles are unique by business rule — CreateTodoListEndpoint.ValidateAsync answers
        // 400 for a title that already exists — and every scenario creates its lists under a
        // fixed title ("My List", "First", ...). Without the reset, the suite passes exactly
        // once per database and then reports 400s for lists it believes are new, and the
        // "Get all todo lists" scenario counts lists left behind by earlier runs. Measured:
        // with this hook emptied, the first run is already 8/10 and the second is 2/10.
        // Lists are plain Marten documents with their items nested inside, so
        // DeleteAllDocuments is the whole reset.
        runner.Resources.Add(new AlbaResource<Program>(reset: host => host.ResetEventStoresAsync()));
    }
}
