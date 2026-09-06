using System.Text.Json.Serialization;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// The CDS Hooks discovery document (`GET {cdsHooksBase}`), as defined by the CDS
/// Hooks specification and profiled by Da Vinci CRD.
///
/// Modelled as plain records rather than hidden behind a helper: a test that reads
/// <c>discovery.Services</c> shows exactly what the external implementation
/// advertised.
/// </summary>
public sealed record CdsHooksDiscovery
{
    [JsonPropertyName("services")] public List<CdsHooksService> Services { get; init; } = new();
}

/// <summary>One advertised CDS Hooks service.</summary>
public sealed record CdsHooksService
{
    [JsonPropertyName("hook")] public string Hook { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("prefetch")] public Dictionary<string, string> Prefetch { get; init; } = new();

    /// <summary>
    /// CRD advertises its supported IG version through the `davinci-crd.version`
    /// discovery extension. Kept as raw JSON so version reporting never depends on
    /// a shape the harness guessed.
    /// </summary>
    [JsonPropertyName("extension")] public Dictionary<string, System.Text.Json.JsonElement> Extension { get; init; } = new();

    /// <summary>The CRD IG versions this service advertises, if it advertises any.</summary>
    public IReadOnlyList<string> AdvertisedCrdVersions
    {
        get
        {
            if (!Extension.TryGetValue("davinci-crd.version", out var value)
                || value.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return value.EnumerateArray()
                .Where(element => element.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(element => element.GetString()!)
                .ToList();
        }
    }
}
