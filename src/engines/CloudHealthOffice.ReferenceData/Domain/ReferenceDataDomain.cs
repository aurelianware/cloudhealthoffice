namespace CloudHealthOffice.ReferenceData.Domain;

/// <summary>A transport-neutral coding that can be projected to FHIR Coding.</summary>
public sealed record ChoCoding
{
    public required string CodeSystem { get; init; }
    public string? CodeSystemUri { get; init; }
    public required string Code { get; init; }
    public string? Version { get; init; }
    public string? Display { get; init; }
}

public enum LicenseClassification
{
    Public,
    Licensed,
    CustomerProvided,
    DevelopmentOnly,
    Restricted,
    Unknown
}

public enum ExposureClassification
{
    PublicReference,
    AuthenticatedReference,
    TenantRestricted,
    InternalOnly
}

/// <summary>
/// A versioned reference code. It deliberately contains no price, benefit, or
/// adjudication fields; those concepts remain in their owning domains.
/// </summary>
public sealed record ReferenceCode
{
    public required string Id { get; init; }
    public string? TenantId { get; init; }
    public required ChoCoding Coding { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public bool Active { get; init; } = true;
    public required string SourceId { get; init; }
    public required string SourceVersion { get; init; }
    public LicenseClassification LicenseClassification { get; init; }
    public ExposureClassification ExposureClassification { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    public required string Checksum { get; init; }

    public bool IsEffectiveOn(DateOnly date) =>
        Active && EffectiveFrom <= date && (EffectiveTo is null || EffectiveTo >= date);
}

public sealed record CodeSystemDefinition(
    string Name,
    string? CanonicalUri,
    LicenseClassification DefaultLicense);

/// <summary>Verified identifiers only. Null means no verified canonical URI is recorded.</summary>
public static class CodeSystemRegistry
{
    private static readonly IReadOnlyDictionary<string, CodeSystemDefinition> Systems =
        new Dictionary<string, CodeSystemDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["CDT"] = new("CDT", "http://www.ada.org/cdt", LicenseClassification.Licensed),
            ["ICD-10-CM"] = new("ICD-10-CM", "http://hl7.org/fhir/sid/icd-10-cm", LicenseClassification.Public),
            ["HCPCS"] = new("HCPCS", "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets", LicenseClassification.Public),
            ["CPT"] = new("CPT", "http://www.ama-assn.org/go/cpt", LicenseClassification.Licensed),
            ["NDC"] = new("NDC", "http://hl7.org/fhir/sid/ndc", LicenseClassification.Public),
            ["CARC"] = new("CARC", "https://x12.org/codes/claim-adjustment-reason-codes", LicenseClassification.Public),
            ["RARC"] = new("RARC", "https://x12.org/codes/remittance-advice-remark-codes", LicenseClassification.Public),
            ["POS"] = new("POS", "https://www.cms.gov/Medicare/Coding/place-of-service-codes", LicenseClassification.Public),
            ["Revenue Codes"] = new("Revenue Codes", null, LicenseClassification.Public),
            ["Provider Taxonomy"] = new("Provider Taxonomy", "http://nucc.org/provider-taxonomy", LicenseClassification.Public)
        };

    public static IReadOnlyCollection<CodeSystemDefinition> All => Systems.Values.ToArray();
    public static bool TryGet(string name, out CodeSystemDefinition definition) =>
        Systems.TryGetValue(name, out definition!);
}
