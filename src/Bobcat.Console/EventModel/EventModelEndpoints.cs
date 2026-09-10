using Bobcat.Console.Contracts;
using Microsoft.AspNetCore.Http;
using Wolverine;
using Wolverine.Http;

namespace Bobcat.Console.EventModel;

/// <summary>
/// The Event Model wire (issue #108): a producer pushes the current descriptor —
/// <c>curl -X PUT --data @event-model.json http://localhost:5525/api/event-model</c> with the
/// file Wolverine's <c>event-model</c> export writes, or a CI step posting what a spec
/// assembly's generated <c>IEventModelDefinitionSource</c> reported — and the SPA's Event
/// Model page reads it back. Like <c>GET /api/runs</c>, this is a public wire contract: the
/// body is a JasperFx <c>EventModelDescriptor</c> in the camelCase/PascalCase-enum shape the
/// shared <c>@jasperfx/event-model-vue</c> renderer types.
/// </summary>
public static class EventModelEndpoints
{
    /// <summary>The current descriptor, or 404 when none has been published yet.</summary>
    [WolverineGet("/api/event-model")]
    public static IResult Get([NotBody] EventModelStore store)
        => store.Read() is { } json
            ? Results.Content(json, "application/json")
            : Results.NotFound();

    /// <summary>
    /// Publish the default source's descriptor — whole document, replacing what that source
    /// published before. 400 with the parse failure when the body is not a descriptor, so a bad
    /// push fails loudly at the push rather than as a blank canvas later.
    /// </summary>
    [WolverinePut("/api/event-model")]
    public static Task<IResult> Put(
        HttpRequest request,
        [NotBody] EventModelStore store,
        [NotBody] IMessageBus bus)
        => PutSource(EventModelStore.DefaultSource, request, store, bus);

    /// <summary>
    /// Publish ONE producer's contribution to the model (CritterWatch#1212). <c>GET</c> serves the
    /// merge of every source, so the host's <c>event-model</c> export and a spec assembly's
    /// generated source can each push their half and the slices fold together by name.
    /// </summary>
    /// <remarks>
    /// A re-push replaces only that source, which is what keeps it idempotent — a producer that
    /// renames or drops a slice actually loses it, instead of leaving the old one behind forever.
    /// </remarks>
    [WolverinePut("/api/event-model/{source}")]
    public static async Task<IResult> PutSource(
        string source,
        HttpRequest request,
        [NotBody] EventModelStore store,
        [NotBody] IMessageBus bus)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);

        var failure = store.TryStore(body, source);
        if (failure is not null)
        {
            return Results.Problem(statusCode: 400, detail: $"Not an EventModelDescriptor: {failure}");
        }

        // #169 — tell any open Event Model page to re-fetch. Only on SUCCESS: a rejected push has
        // not changed what the page would load, and announcing it would make the diagram flicker
        // for a document nobody stored.
        await bus.PublishAsync(new EventModelChanged(store.Name ?? string.Empty));

        return Results.NoContent();
    }
}
