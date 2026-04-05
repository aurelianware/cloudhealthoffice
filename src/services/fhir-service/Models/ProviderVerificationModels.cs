namespace FhirService.Models;

/// <summary>
/// Lightweight summary used by PAS controller for provider pre-check.
/// Not the full ProviderVerificationRecord — just what PAS needs to make a go/no-go decision.
/// </summary>
public class ProviderVerificationSummary
{
    public int IntegrityScore { get; set; }
    public string Rating { get; set; } = "Unknown";
    public bool IsExcluded { get; set; }
    public string? ExclusionSource { get; set; }
    public string Status { get; set; } = "Unknown";
}

/// <summary>
/// Maps to the JSON response from GET /api/v1/providers/{npi}/integrity-score
/// </summary>
public class ProviderIntegrityResponse
{
    public string Npi { get; set; } = string.Empty;
    public int CompositeScore { get; set; }
    public string Rating { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<ProviderIntegrityFlag>? Flags { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
}

public class ProviderIntegrityFlag
{
    public string Severity { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
