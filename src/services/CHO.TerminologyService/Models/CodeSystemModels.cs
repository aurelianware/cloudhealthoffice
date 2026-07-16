namespace CHO.TerminologyService.Models;

/// <summary>
/// A displayable code-system concept, stored independently from ConceptMap crosswalks.
/// </summary>
public class CodeSystemConcept
{
    public string Id { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string Source { get; set; } = "CodeSystemCatalog";
    public string? TenantId { get; set; }
    public bool IsOverride { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed record CodeSystemDisplay(string Display, string? Version, string Source);
