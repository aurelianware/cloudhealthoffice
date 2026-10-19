using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

/// <summary>Reads an authorization status given as a name or a number into its number.</summary>
public sealed class AuthorizationStatusNumberConverter : System.Text.Json.Serialization.JsonConverter<int>
{
    private static readonly string[] Names =
        { "Unknown", "Submitted", "InReview", "Pended", "Approved", "Modified", "Denied", "Expired", "Cancelled" };

    public override int Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number) return reader.GetInt32();
        if (reader.TokenType == System.Text.Json.JsonTokenType.String)
        {
            var text = reader.GetString();
            if (int.TryParse(text, out var number)) return number;
            var index = Array.FindIndex(Names, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? 0 : index;
        }
        return 0;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, int value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
