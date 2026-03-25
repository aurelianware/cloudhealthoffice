using System.Text.Json.Serialization;

namespace ClaimsScrubbingService.Models;

public class ComponentHealth
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty; // "healthy" | "degraded" | "unhealthy"

    [JsonPropertyName("latencyMs")]
    public long? LatencyMs { get; set; }

    [JsonPropertyName("lastCheck")]
    public string LastCheck { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("details")]
    public Dictionary<string, object>? Details { get; set; }
}

public class ServiceMetrics
{
    [JsonPropertyName("claimsProcessed")]
    public long ClaimsProcessed { get; set; }

    [JsonPropertyName("claimsClean")]
    public long ClaimsClean { get; set; }

    [JsonPropertyName("claimsFlagged")]
    public long ClaimsFlagged { get; set; }

    [JsonPropertyName("claimsRejected")]
    public long ClaimsRejected { get; set; }

    [JsonPropertyName("averageValidationTimeMs")]
    public double AverageValidationTimeMs { get; set; }

    [JsonPropertyName("firstPassRate")]
    public double FirstPassRate { get; set; }
}
