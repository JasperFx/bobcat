using Bobcat;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace CleanArchitectureTodos.Tests;

/// <summary>
/// Suite configuration for this spec project. No <c>Main</c>: the project references
/// <c>Bobcat.Mtp</c> and declares no entry point, so the generator emits the
/// Microsoft.Testing.Platform one and calls every <c>[BobcatConfiguration]</c> method.
/// Declaring none is also what keeps <c>WebApp</c>'s unqualified <c>Program</c> unambiguous —
/// see docs/resources.md.
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
        runner.Resources.Add(new WebApp(reset: async host =>
        {
            var store = host.Services.GetRequiredService<IDocumentStore>();
            await store.Advanced.Clean.DeleteAllDocumentsAsync();
        }));
    }
}
