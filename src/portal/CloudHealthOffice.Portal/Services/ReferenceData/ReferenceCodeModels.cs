namespace CloudHealthOffice.Portal.Services;

// Shapes returned by reference-data-service (api/reference-data/codes).

public class ReferenceCodeCoding
{
    public string CodeSystem { get; set; } = string.Empty;
    public string? CodeSystemUri { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Display { get; set; }
}

public class ReferenceCodeRecord
{
    public string Id { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public ReferenceCodeCoding Coding { get; set; } = new();
    public string? Description { get; set; }
    public string? Category { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public bool Active { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string SourceVersion { get; set; } = string.Empty;
    public string LicenseClassification { get; set; } = string.Empty;
    public DateTimeOffset ImportedAt { get; set; }

    public string CodeSystem => Coding.CodeSystem;
    public string Code => Coding.Code;
    public string Text => Description ?? Coding.Display ?? string.Empty;
}

public class ReferenceCodePage
{
    public List<ReferenceCodeRecord> Items { get; set; } = new();
    public int Total { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
}

/// <summary>How the search term matches: the whole code, the start of the code, or words in the description.</summary>
public enum ReferenceSearchMode { Exact, Prefix, Text }

public static class ReferenceCodeSystems
{
    /// <summary>The code systems reference-data-service knows (CodeSystemRegistry).</summary>
    public static readonly IReadOnlyList<(string Name, string Label)> All =
    [
        ("ICD-10-CM", "ICD-10-CM (diagnoses)"),
        ("CPT", "CPT (procedures)"),
        ("HCPCS", "HCPCS (supplies and services)"),
        ("CDT", "CDT (dental)"),
        ("NDC", "NDC (drugs)"),
        ("Revenue Codes", "Revenue codes"),
        ("POS", "Place of service"),
        ("CARC", "Claim adjustment reasons (CARC)"),
        ("RARC", "Remittance remarks (RARC)"),
        ("Provider Taxonomy", "Provider taxonomy"),
    ];
}
