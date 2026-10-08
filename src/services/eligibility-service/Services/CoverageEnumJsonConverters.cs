using System.Text.Json;
using System.Text.Json.Serialization;

namespace EligibilityService.Services;

/// <summary>
/// coverage-service serializes its enums by name (shared
/// <c>AddCloudHealthOfficeJsonOptions</c>: <c>"status":"Active"</c>,
/// <c>"lineOfBusiness":"Commercial"</c>), while eligibility-service's coverage
/// DTOs carry them as coverage-service's underlying ints. Reading a name into
/// an <c>int</c> property throws, so these converters accept either the name or
/// the number. Unknown names read as 0 (not in force / unknown).
/// </summary>
public abstract class CoverageEnumIntConverter : JsonConverter<int>
{
    private readonly IReadOnlyDictionary<string, int> _byName;

    protected CoverageEnumIntConverter(IReadOnlyDictionary<string, int> byName) => _byName = byName;

    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetInt32();
            case JsonTokenType.String:
                var name = reader.GetString();
                if (string.IsNullOrEmpty(name)) return 0;
                if (int.TryParse(name, out var number)) return number;
                return _byName.TryGetValue(name, out var value) ? value : 0;
            case JsonTokenType.Null:
                return 0;
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for a coverage enum");
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary>coverage-service <c>CoverageStatus</c>: Active=1, Pending=2, Terminated=3, Suspended=4, COBRA=5.</summary>
public sealed class CoverageStatusIntConverter() : CoverageEnumIntConverter(
    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Active"] = 1,
        ["Pending"] = 2,
        ["Terminated"] = 3,
        ["Suspended"] = 4,
        ["COBRA"] = 5
    });

/// <summary>coverage-service <c>LineOfBusiness</c>: Commercial=1 … VA=6.</summary>
public sealed class CoverageLineOfBusinessIntConverter() : CoverageEnumIntConverter(
    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Commercial"] = 1,
        ["Medicare"] = 2,
        ["Medicaid"] = 3,
        ["Exchange"] = 4,
        ["TRICARE"] = 5,
        ["VA"] = 6
    });
