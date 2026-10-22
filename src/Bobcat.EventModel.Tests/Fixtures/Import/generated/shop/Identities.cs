// Imported from an event model by `bobcat import-event-model --strong-typed-ids`.
//
// One identity type per aggregate, wrapping the identity its stream would otherwise have.
// Nothing regenerates this file, so your edits are safe.

#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shop;

/// <summary>The identity of Order, and of everything that addresses its stream.</summary>
[JsonConverter(typeof(OrderId.Converter))]
public readonly record struct OrderId(Guid Value) : IParsable<OrderId>
{
    public override string ToString() => Value.ToString();

    public static OrderId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out OrderId result)
    {
        var parsed = Guid.TryParse(s, out var value);
        result = new OrderId(value);
        return parsed;
    }

    /// <summary>So the wire carries the value, not an object wrapping it.</summary>
    public sealed class Converter : JsonConverter<OrderId>
    {
        public override OrderId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetGuid());

        public override void Write(Utf8JsonWriter writer, OrderId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }
}
