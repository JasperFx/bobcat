using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Events.EventModeling;

namespace Bobcat.Ui.EventModel;

/// <summary>
/// Reads a slice's <c>pattern</c> without ever refusing the descriptor over it. A name this build's
/// JasperFx does not know — a member a newer producer appended, as JasperFx 2.85.0 appended
/// <c>Query</c> — reads as an underived pattern (null) rather than failing the whole push with a
/// 400, or the whole stored model as unreadable. A number reads as the ordinal, defined or not,
/// exactly as the stock enum converter allows. Writes the PascalCase name for a member this build
/// names and the ordinal for one it does not, so a stored copy keeps what it was given.
/// </summary>
public sealed class TolerantSlicePatternConverter : JsonConverter<SlicePattern?>
{
    public override bool HandleNull => true;

    public override SlicePattern? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number when reader.TryGetInt32(out var ordinal):
                return (SlicePattern)ordinal;

            case JsonTokenType.String:
                var text = reader.GetString();
                return Enum.TryParse<SlicePattern>(text, ignoreCase: true, out var pattern)
                       && !int.TryParse(text, out _)
                    ? pattern
                    : null;

            default:
                // An object or array where a pattern belongs: skip it whole so the reader stays in step.
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, SlicePattern? value, JsonSerializerOptions options)
    {
        if (value is not { } pattern)
        {
            writer.WriteNullValue();
            return;
        }

        if (Enum.IsDefined(pattern)) writer.WriteStringValue(pattern.ToString());
        else writer.WriteNumberValue((int)pattern);
    }
}
