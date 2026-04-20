using System;
using System.ComponentModel.DataAnnotations;
using MongoDB.Bson.Serialization.Attributes;

namespace MemberService.Models;

/// <summary>
/// A flag attached to a member that warns or constrains downstream interactions
/// (litigation hold, custody dispute, accessibility need, fraud risk, etc.).
/// Projects to FHIR R4 Flag.
///
/// Lifecycle: alerts are never deleted. They are created with a <see cref="StartDate"/>
/// and end-dated by writing <see cref="EndDate"/>. The active set is computed as
/// <c>StartDate &lt;= now AND (EndDate IS NULL OR EndDate &gt; now)</c>.
/// </summary>
[BsonIgnoreExtraElements]
public class MemberAlert
{
    /// <summary>Multi-tenant partition key.</summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Cosmos document id.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>External member id (matches <see cref="Member.MemberId"/>).</summary>
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    [Required]
    public MemberAlertType AlertType { get; set; }

    [Required]
    public MemberAlertSeverity Severity { get; set; } = MemberAlertSeverity.Info;

    /// <summary>When the alert becomes effective. Defaults to creation time.</summary>
    [Required]
    public DateTime StartDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the alert was end-dated. Null while active. Once set, the alert is no
    /// longer surfaced as active and downstream block rules stop applying.
    /// </summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Free-text reason the alert was raised. Required.</summary>
    [Required]
    [StringLength(2000)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Optional operator-facing instruction (e.g., "Route all communication
    /// through legal" or "Confirm CSR speaks Spanish before transferring").
    /// </summary>
    [StringLength(2000)]
    public string? RequiredAction { get; set; }

    /// <summary>User or system that created the alert.</summary>
    [Required]
    [StringLength(200)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>User or system that end-dated the alert. Set when <see cref="EndDate"/> is set.</summary>
    [StringLength(200)]
    public string? EndedBy { get; set; }

    /// <summary>True when the alert is currently in effect.</summary>
    public bool IsActive(DateTime? asOf = null)
    {
        var t = asOf ?? DateTime.UtcNow;
        return StartDate <= t && (!EndDate.HasValue || EndDate.Value > t);
    }
}

/// <summary>
/// Member alert taxonomy. Each value maps to a FHIR Flag.code coding (see
/// <see cref="MemberService.Services.FhirFlagProjector"/>).
/// </summary>
public enum MemberAlertType
{
    HighRisk = 1,
    LitigationHold = 2,
    DoNotContact = 3,
    VIP = 4,
    CustodyDispute = 5,
    LanguageRequirement = 6,
    AccessibilityNeed = 7,
    SecurityFreeze = 8,
    KnownFraudRisk = 9,
    EligibilityDispute = 10
}

/// <summary>
/// Severity drives color in the portal banner and the FHIR Flag.category.
/// Severity is also compared against a per-rule minimum in
/// <see cref="MemberService.Services.IMemberAlertGuard"/> — e.g., Terminate
/// is blocked by a LitigationHold at Critical severity, and by an
/// EligibilityDispute at Warning or higher. Numeric order is significant:
/// the evaluator uses <c>severity &gt;= minSeverity</c>.
/// </summary>
public enum MemberAlertSeverity
{
    Info = 1,
    Warning = 2,
    Critical = 3
}
