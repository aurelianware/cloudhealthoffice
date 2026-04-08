using System.Text.Json.Serialization;
using CloudHealthOffice.ProviderEnrollmentService.Models;

namespace CloudHealthOffice.ProviderEnrollmentService.Cache;

/// <summary>
/// Cosmos DB document wrapper for StateEnrollmentRecord.
///
/// Container: enrollment-cache
/// Partition key: /stateCode
///   — enrollment queries are always state-scoped, so this partition strategy
///     keeps cross-provider queries within a single logical partition per state.
///
/// Document ID: "{npi}::{stateCode}" (e.g., "1234567890::TX")
///
/// TTL: controlled by ProviderEnrollmentOptions.CacheTtl.
///      Set Cosmos container DefaultTimeToLive = -1 (inherit from document).
/// </summary>
public sealed class EnrollmentCacheDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("stateCode")]
    public string StateCode { get; init; } = string.Empty;   // partition key

    [JsonPropertyName("npi")]
    public string Npi { get; init; } = string.Empty;

    [JsonPropertyName("sourceSystem")]
    public string SourceSystem { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("effectiveDate")]
    public string EffectiveDate { get; init; } = string.Empty;

    [JsonPropertyName("terminationDate")]
    public string? TerminationDate { get; init; }

    [JsonPropertyName("revalidationDueDate")]
    public string? RevalidationDueDate { get; init; }

    [JsonPropertyName("lastVerifiedDate")]
    public string? LastVerifiedDate { get; init; }

    [JsonPropertyName("providerType")]
    public string ProviderType { get; init; } = string.Empty;

    [JsonPropertyName("supportedLobs")]
    public int SupportedLobs { get; init; }

    [JsonPropertyName("enrolledTaxonomies")]
    public IReadOnlyList<string> EnrolledTaxonomies { get; init; } = [];

    [JsonPropertyName("enrolledCounties")]
    public IReadOnlyList<string> EnrolledCounties { get; init; } = [];

    [JsonPropertyName("enrolledZipCodes")]
    public IReadOnlyList<string> EnrolledZipCodes { get; init; } = [];

    [JsonPropertyName("mcoParticipation")]
    public IReadOnlyList<string> McoParticipation { get; init; } = [];

    [JsonPropertyName("restrictions")]
    public IReadOnlyList<RestrictionDocument> Restrictions { get; init; } = [];

    [JsonPropertyName("cachedAt")]
    public DateTime CachedAt { get; init; }

    /// <summary>Cosmos TTL in seconds. Set from ProviderEnrollmentOptions.CacheTtl.</summary>
    [JsonPropertyName("ttl")]
    public int Ttl { get; init; }

    // ── Mapping helpers ───────────────────────────────────────────

    public static EnrollmentCacheDocument FromRecord(StateEnrollmentRecord r, TimeSpan ttl) => new()
    {
        Id                  = MakeId(r.Npi, r.StateCode),
        StateCode           = r.StateCode,
        Npi                 = r.Npi,
        SourceSystem        = r.SourceSystem,
        Status              = r.Status.ToString(),
        EffectiveDate       = r.EffectiveDate.ToString("O"),
        TerminationDate     = r.TerminationDate?.ToString("O"),
        RevalidationDueDate = r.RevalidationDueDate?.ToString("O"),
        LastVerifiedDate    = r.LastVerifiedDate?.ToString("O"),
        ProviderType        = r.ProviderType.ToString(),
        SupportedLobs       = (int)r.SupportedLobs,
        EnrolledTaxonomies  = r.EnrolledTaxonomies,
        EnrolledCounties    = r.EnrolledCounties,
        EnrolledZipCodes    = r.EnrolledZipCodes,
        McoParticipation    = r.McoParticipation,
        Restrictions        = r.Restrictions.Select(RestrictionDocument.From).ToList(),
        CachedAt            = DateTime.UtcNow,
        Ttl                 = (int)ttl.TotalSeconds
    };

    public StateEnrollmentRecord ToRecord() => new()
    {
        Npi                 = Npi,
        StateCode           = StateCode,
        SourceSystem        = SourceSystem,
        Status              = Enum.Parse<EnrollmentStatus>(Status),
        EffectiveDate       = DateOnly.Parse(EffectiveDate),
        TerminationDate     = TerminationDate is null ? null : DateOnly.Parse(TerminationDate),
        RevalidationDueDate = RevalidationDueDate is null ? null : DateOnly.Parse(RevalidationDueDate),
        LastVerifiedDate    = LastVerifiedDate is null ? null : DateOnly.Parse(LastVerifiedDate),
        ProviderType        = Enum.Parse<ProviderTypeClassification>(ProviderType),
        SupportedLobs       = (LineOfBusiness)SupportedLobs,
        EnrolledTaxonomies  = EnrolledTaxonomies,
        EnrolledCounties    = EnrolledCounties,
        EnrolledZipCodes    = EnrolledZipCodes,
        McoParticipation    = McoParticipation,
        Restrictions        = Restrictions.Select(r => r.ToRestriction()).ToList(),
        CachedAt            = CachedAt,
        IsFromCache         = true
    };

    public static string MakeId(string npi, string stateCode) => $"{npi}::{stateCode}";
}

public sealed class RestrictionDocument
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
    [JsonPropertyName("effectiveDate")]
    public string? EffectiveDate { get; init; }
    [JsonPropertyName("liftDate")]
    public string? LiftDate { get; init; }

    public static RestrictionDocument From(EnrollmentRestriction r) => new()
    {
        Type         = r.Type.ToString(),
        Description  = r.Description,
        EffectiveDate = r.EffectiveDate?.ToString("O"),
        LiftDate      = r.LiftDate?.ToString("O")
    };

    public EnrollmentRestriction ToRestriction() => new()
    {
        Type         = Enum.Parse<RestrictionType>(Type),
        Description  = Description,
        EffectiveDate = EffectiveDate is null ? null : DateOnly.Parse(EffectiveDate),
        LiftDate      = LiftDate is null ? null : DateOnly.Parse(LiftDate)
    };
}
