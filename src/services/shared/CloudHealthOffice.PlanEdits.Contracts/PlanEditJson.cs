using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudHealthOffice.PlanEdits.Contracts;

/// <summary>
/// The JSON form of the contract: camelCase names, absent values omitted,
/// unknown fields ignored (a later minor addition never breaks a reader).
/// </summary>
public static class PlanEditJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        // Numbers as numbers only: "units": "2" is a contract violation, not a 2.
        NumberHandling = JsonNumberHandling.Strict,
    };
}
