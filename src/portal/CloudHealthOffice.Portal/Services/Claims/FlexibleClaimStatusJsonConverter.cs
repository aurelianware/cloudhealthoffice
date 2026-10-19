using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Driver;

namespace CloudHealthOffice.Portal.Services;

public sealed class FlexibleClaimStatusJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number when reader.TryGetInt32(out var value) => value switch
            {
                1 => "Submitted",
                2 => "Received",
                3 => "InAdjudication",
                4 => "Pended",
                5 => "Approved",
                6 => "Denied",
                7 => "Paid",
                8 => "Voided",
                9 => "PartiallyPaid",
                10 => "Deny",
                _ => value.ToString()
            },
            JsonTokenType.Number => reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            JsonTokenType.Null => string.Empty,
            _ => JsonDocument.ParseValue(ref reader).RootElement.ToString()
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
