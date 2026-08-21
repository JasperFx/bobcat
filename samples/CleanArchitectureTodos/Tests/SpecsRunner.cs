using Bobcat.Alba;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace CleanArchitectureTodos.Tests;

/// <summary>
/// Bobcat spec-runner entry point. An explicit Main class rather than top-level statements,
/// because this project references the host — which uses top-level statements and synthesizes
/// its own <c>Program</c> in the global namespace. Two of those in one assembly make
/// <c>AlbaResource&lt;Program&gt;</c> bind to the runner stub instead of the web app, which
/// surfaces as a native PAL crash with no managed stack. See docs/sample-wiring.md footgun 1.
/// </summary>
public static class SpecsRunner
{
    public static Task<int> Main(string[] args)
        => BobcatRunner.Run(args, runner =>
        {
            // Resolves unambiguously to the host's entry point, given the explicit Main above.
            //
            // The reset hook is load-bearing here, the same way it is in OutboxDemo. Todo list
            // titles are unique by business rule — CreateTodoListEndpoint.ValidateAsync answers
            // 400 for a title that already exists — and every scenario creates its lists under a
            // fixed title ("My List", "First", ...). Without the reset, the suite passes exactly
            // once per database and then reports 400s for lists it believes are new, and the
            // "Get all todo lists" scenario counts lists left behind by earlier runs. Measured:
            // with this hook emptied, the first run is already 8/10 and the second is 2/10.
            // Lists are plain Marten documents with their items nested inside, so
            // DeleteAllDocuments is the whole reset.
            runner.Suite.AddResource(new AlbaResource<Program>(reset: async host =>
            {
                var store = host.Services.GetRequiredService<IDocumentStore>();
                await store.Advanced.Clean.DeleteAllDocumentsAsync();
            }));
            runner.ScanForFeatures(typeof(CleanArchitectureTodosFixture).Assembly);
        });
}
