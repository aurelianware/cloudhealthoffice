using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public sealed class CoverageLineOfBusinessValueConverter : System.Text.Json.Serialization.JsonConverter<int>
{
    public override int Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number && reader.TryGetInt32(out var numericValue))
        {
            return numericValue;
        }

        if (reader.TokenType == System.Text.Json.JsonTokenType.String)
        {
            return reader.GetString()?.Trim().ToLowerInvariant() switch
            {
                "commercial" => 1,
                "medicare" => 2,
                "medicaid" => 3,
                "exchange" => 4,
                "tricare" => 5,
                "va" => 6,
                _ => 0
            };
        }

        return 0;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, int value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
