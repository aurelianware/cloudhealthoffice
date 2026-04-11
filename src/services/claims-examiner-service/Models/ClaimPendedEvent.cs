namespace ClaimsExaminerService.Models;

/// <summary>
/// Inbound Kafka payload from claims-service. Mirrors claims-service's
/// ClaimsService.Services.ClaimPendedEvent shape — kept local on purpose so
/// the examiner service does not take a build-time dependency on the producer.
/// Schema is versioned by topic name (claims.pended.v1); a breaking change to
/// this shape requires a new topic, never an in-place mutation.
/// </summary>
public class ClaimPendedEvent
{
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string EventVersion { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }

    public string TenantId { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string BillingProviderNPI { get; set; } = string.Empty;
    public int LineOfBusiness { get; set; }
    public decimal TotalChargeAmount { get; set; }
    public DateTime ServiceDateFrom { get; set; }

    public PendDetails? PendDetails { get; set; }
}

/// <summary>
/// Pend reason carried on the event. Must match claims-service shape.
/// </summary>
public class PendDetails
{
    public string PendCode { get; set; } = string.Empty;
    public string? PendReason { get; set; }
    public DateTime PendedAt { get; set; }
    public List<NcciEditFailureSnapshot> EditFailures { get; set; } = new();
}

/// <summary>
/// NCCI/MUE edit failure as carried on the event. Local copy of the snapshot
/// shape claims-service stores on Claim.PendDetails.EditFailures.
/// </summary>
public class NcciEditFailureSnapshot
{
    public string EditType { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? Column1Code { get; set; }
    public string? Column2Code { get; set; }
    public List<int> AffectedLineNumbers { get; set; } = new();
    public bool ModifierOverridePresent { get; set; }
    public decimal? UnitsBilled { get; set; }
    public int? MueMaxUnits { get; set; }
    public string? SuggestedCarc { get; set; }
    public string? SuggestedRarc { get; set; }

    /// <summary>
    /// True when the edit type is one a -59/X{EPSU} modifier could legally override.
    /// V1 of the examiner only acts on NCCI pair edits (RuleId NE001) — MUE / unit
    /// limit edits are out of scope for v1 because they have no modifier override path.
    /// </summary>
    public bool IsModifierAddressable() =>
        string.Equals(EditType, "NcciPair", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(RuleId, "NE001", StringComparison.OrdinalIgnoreCase);
}
