using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bobcat.EventModel;

/// <summary>
/// The public wire shape for an <c>EventModelDescriptor</c>: camelCase members, PascalCase enum
/// values. Every producer and every consumer of <c>PUT/GET /api/event-model</c> serializes through
/// this, and so does the <c>import-event-model</c> push.
/// </summary>
/// <remarks>
/// <para>
/// It lives in the format library rather than in whichever console happens to store the document,
/// because the spelling is a property of the CONTRACT and not of any one holder of it. Two viewers
/// and a CLI agree on these options; only one of them owns a store.
/// </para>
/// <para>
/// ⚠️ The enum casing is load-bearing and fails SILENTLY when it is wrong. ASP.NET's default web
/// JSON applies <c>JsonStringEnumConverter(JsonNamingPolicy.CamelCase)</c>, which puts
/// <c>"observed"</c> on the wire where <c>@jasperfx/event-model-vue</c> matches <c>"Observed"</c> —
/// the renderer then draws no provenance wedge and no hotspot outline, and no typecheck can see it
/// because the JSON arrives untyped. CritterWatch hit exactly this and answers it with its own
/// <c>StoreJsonResults</c>. Never let a framework default serialize this document.
/// </para>
/// <para>
/// Reading is enum-case-insensitive, so a producer that serializes camelCase enum values is
/// normalized rather than rejected.
/// </para>
/// </remarks>
public static class EventModelWire
{
    /// <summary>The serializer options every producer and consumer of the model document shares.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(namingPolicy: null) }
    };

    /// <summary>The source name a bare <c>PUT /api/event-model</c> writes to.</summary>
    public const string DefaultSource = "default";
}
