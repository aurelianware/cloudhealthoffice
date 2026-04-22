using System;
using System.ComponentModel.DataAnnotations;
using MongoDB.Bson.Serialization.Attributes;

// TODO(feature-5.18-followup): FHIR Consent R4 projection. Not in this PR —
// the follow-on ticket will project this entity onto FHIR Consent.provision
// and FHIR Consent.policyRule. No projection interface is scaffolded here;
// keeping the entity first-party until we know what the projection needs.

namespace ConsentService.Models;

/// <summary>
/// A HIPAA §164.508 authorization record (and adjacent consent types —
/// see <see cref="Models.ConsentType"/>). Consents are never hard-deleted;
/// lifecycle transitions (Draft → Active → Revoked / Expired) are captured
/// through <see cref="Repositories.IConsentRepository.TransitionStatusAsync"/>
/// with a matching <see cref="ConsentEvent"/> appended atomically for audit.
///
/// PHI-adjacent free-text fields (<see cref="Reason"/>, <see cref="GrantedToName"/>,
/// <see cref="GrantedToContact"/>, <see cref="Purpose"/>) are stored as
/// ciphertext; encryption is applied by the controller layer before
/// persistence, decryption on read-back. The fields are always accessed
/// through the entity in ciphertext form — repositories do not decrypt.
/// </summary>
[BsonIgnoreExtraElements]
public class Consent
{
    /// <summary>Multi-tenant partition key.</summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Stable consent id. Cosmos document id and Mongo `_id`.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>External member id (matches <c>Member.MemberId</c> in member-service).</summary>
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    [Required]
    public ConsentType ConsentType { get; set; }

    /// <summary>
    /// Optional sub-category when the record falls under a heightened regulatory
    /// regime (42 CFR Part 2, state-law sensitive categories). Free-form string
    /// so the enum stays stable across sub-type additions. Known values:
    /// <c>HIV</c>, <c>SubstanceAbuse</c>, <c>MentalHealth</c>, <c>Genetic</c>.
    /// </summary>
    [StringLength(100)]
    public string? SensitiveCategory { get; set; }

    [Required]
    public ConsentStatus Status { get; set; } = ConsentStatus.Draft;

    /// <summary>When the authorization becomes effective. Required for Active.</summary>
    public DateTime? EffectiveAt { get; set; }

    /// <summary>When the authorization expires. Optional; unbounded when null.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Name or identifier of the person granting the authorization.
    /// TODO(feature-5.18-followup): when feature 5.8 Personal Representative
    /// delegation lands, this may become a structured reference rather than
    /// a free string. Keeping as <c>string(200)</c> until the delegation model
    /// exists — no relationship shim in this PR.
    /// </summary>
    [Required]
    [StringLength(200)]
    public string GrantedBy { get; set; } = string.Empty;

    // ── Encrypted at rest — never included in Kafka event payload ───────

    /// <summary>
    /// Free-text reason supplied by the grantor. Encrypted at rest via
    /// <see cref="Services.IConsentFieldEncryptor"/>.
    /// </summary>
    [StringLength(4000)]
    public string? Reason { get; set; }

    /// <summary>
    /// Name of the party the authorization is granted to. Encrypted at rest.
    /// </summary>
    [StringLength(500)]
    public string? GrantedToName { get; set; }

    /// <summary>
    /// Contact (email, phone, mailing address) for the party the authorization
    /// is granted to. Encrypted at rest.
    /// </summary>
    [StringLength(1000)]
    public string? GrantedToContact { get; set; }

    /// <summary>
    /// Free-text description of the purpose of the authorization. Encrypted at rest.
    /// </summary>
    [StringLength(2000)]
    public string? Purpose { get; set; }

    // ── Lifecycle timestamps ────────────────────────────────────────────

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? ActivatedBy { get; set; }
    public DateTime? ActivatedAt { get; set; }

    [StringLength(200)]
    public string? RevokedBy { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Controlled reason code for revocation. Safe to include in event payload
    /// (enum-valued). See <see cref="ConsentRevocationReasonCode"/>.
    /// </summary>
    public ConsentRevocationReasonCode? RevocationReasonCode { get; set; }

    /// <summary>
    /// Projects the persisted status into the status the caller observes —
    /// <see cref="ConsentStatus.Expired"/> whenever the record is still
    /// persisted as <see cref="ConsentStatus.Active"/> but the effective
    /// <see cref="ExpiresAt"/> has passed. The observed-transition write to
    /// the persisted status happens in
    /// <see cref="Repositories.IConsentRepository.TryTransitionToExpiredAsync"/>.
    /// </summary>
    public ConsentStatus ObservedStatus(DateTime? asOf = null)
    {
        var t = asOf ?? DateTime.UtcNow;
        if (Status == ConsentStatus.Active && ExpiresAt.HasValue && ExpiresAt.Value <= t)
            return ConsentStatus.Expired;
        return Status;
    }
}

/// <summary>
/// Consent type. <see cref="TpoDisclosure"/> is tracked here as a pragmatic
/// default; whether TPO disclosures belong on consent-service or on a
/// separate audit primitive is flagged for legal review before merge.
/// </summary>
public enum ConsentType
{
    /// <summary>
    /// §164.506 Treatment/Payment/Operations disclosure. Strictly speaking
    /// does not require §164.508 authorization; recorded here as an audit
    /// primitive. Confirm with legal whether this belongs on consent-service.
    /// </summary>
    TpoDisclosure = 1,

    /// <summary>Standard §164.508 authorization.</summary>
    GeneralAuthorization = 2,

    /// <summary>
    /// Authorization for disclosure of sensitive categories governed by
    /// 42 CFR Part 2 and/or state law (HIV, substance abuse, mental health,
    /// genetic). The exact category is carried on
    /// <see cref="Consent.SensitiveCategory"/>.
    /// </summary>
    SensitiveCategoryAuthorization = 3
}

/// <summary>
/// Consent lifecycle state. See <c>Services.ConsentStateMachine</c> for the
/// allowed transitions; <see cref="Expired"/> is a read-time projection of
/// <see cref="Active"/> once <see cref="Consent.ExpiresAt"/> passes.
/// </summary>
public enum ConsentStatus
{
    Draft = 1,
    Active = 2,
    Revoked = 3,
    Expired = 4
}

/// <summary>
/// Controlled revocation reasons. Safe to include in event payloads (no PHI).
/// </summary>
public enum ConsentRevocationReasonCode
{
    MemberRequest = 1,
    Expired = 2,
    SupersededByNewConsent = 3,
    AdminError = 4,
    Other = 99
}

/// <summary>
/// Thrown when a caller attempts a consent lifecycle transition that is not
/// allowed by <c>Services.ConsentStateMachine</c>. Distinct from a generic
/// <see cref="InvalidOperationException"/> so the controller layer can map
/// it to a 409 Conflict with <see cref="FromStatus"/>/<see cref="ToStatus"/>
/// in ProblemDetails rather than a 500.
/// </summary>
public sealed class InvalidConsentTransitionException : InvalidOperationException
{
    public ConsentStatus FromStatus { get; }
    public ConsentStatus ToStatus { get; }

    public InvalidConsentTransitionException(ConsentStatus from, ConsentStatus to)
        : base($"Consent transition {from} -> {to} is not allowed.")
    {
        FromStatus = from;
        ToStatus = to;
    }
}
