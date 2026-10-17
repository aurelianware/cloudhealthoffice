using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Driver;

namespace CloudHealthOffice.Portal.Services;

public sealed class FlexibleDecimalJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetDecimal(out var value) => value,
            JsonTokenType.Number => Convert.ToDecimal(reader.GetDouble()),
            JsonTokenType.String when decimal.TryParse(
                reader.GetString(),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) => value,
            JsonTokenType.String => 0m,
            JsonTokenType.Null => 0m,
            JsonTokenType.True => 1m,
            JsonTokenType.False => 0m,
            _ => 0m
        };
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}
