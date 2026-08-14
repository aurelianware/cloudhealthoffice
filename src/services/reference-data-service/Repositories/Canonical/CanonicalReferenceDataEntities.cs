using CloudHealthOffice.ReferenceData.Domain;

namespace ReferenceDataService.Repositories.Canonical;

public sealed class CanonicalReferenceCodeEntity
{
    public string StorageKey { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string CodeSystem { get; set; } = string.Empty;
    public string? CodeSystemUri { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Display { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public bool Active { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string SourceVersion { get; set; } = string.Empty;
    public LicenseClassification LicenseClassification { get; set; }
    public ExposureClassification ExposureClassification { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public string Checksum { get; set; } = string.Empty;
}

public sealed class CanonicalReferenceDataImportEntity
{
    public string ImportKey { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string SourceVersion { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public DateTimeOffset ImportedAt { get; set; }
    public int RecordCount { get; set; }
}
