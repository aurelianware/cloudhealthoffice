using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public sealed class CoverageStatusValueConverter : System.Text.Json.Serialization.JsonConverter<int>
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
                "active" => 1,
                "pending" => 2,
                "terminated" => 3,
                "suspended" => 4,
                "cobra" => 5,
                _ => 0
            };
        }

        return 0;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, int value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
