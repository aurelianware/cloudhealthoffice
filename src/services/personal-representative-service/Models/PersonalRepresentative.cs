using System;
using System.ComponentModel.DataAnnotations;
using MongoDB.Bson.Serialization.Attributes;

// TODO(signature-capture-followup): Proof-of-authority document signature
// capture / notarization verification is out of scope for this PR.
// ProofOfAuthorityDocumentId is a UUID pointer into member-document-service;
// bytes live there, we hold the reference.
// TODO(fhir-related-person-followup): FHIR R4 RelatedPerson projection
// belongs in fhir-service, not here. No projection interface is scaffolded
// on this entity — keeping the model first-party until we know what the
// projection needs.

namespace PersonalRepresentativeService.Models;

/// <summary>
/// An individual with legal authority to act on behalf of a member under
/// 45 CFR §164.502(g) — parent of a minor, court-appointed legal guardian,
/// holder of a healthcare power of attorney, or a healthcare surrogate.
/// One rep record may be associated with multiple members via
/// <see cref="PersonalRepAssociation"/>.
///
/// Personal Representatives are never hard-deleted; lifecycle transitions
/// (Draft → Active → Inactive) are captured through
/// <see cref="Repositories.IPersonalRepRepository.TransitionStatusAsync"/>
/// with a matching <see cref="PersonalRepEvent"/> appended atomically for
/// audit.
///
/// PHI-adjacent fields (name, contact, address, notes) are stored as
/// ciphertext; encryption is applied by the controller layer before
/// persistence, decryption on read-back. The fields are always accessed
/// through the entity in ciphertext form — repositories do not decrypt.
/// </summary>
[BsonIgnoreExtraElements]
public class PersonalRepresentative
{
    /// <summary>Multi-tenant partition key.</summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Stable rep id. Cosmos document id and Mongo `_id`.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public PersonalRepStatus Status { get; set; } = PersonalRepStatus.Draft;

    [Required]
    public PersonalRepCredentialType CredentialType { get; set; }

    /// <summary>When the rep's authority becomes effective.</summary>
    public DateTime? EffectiveFrom { get; set; }

    /// <summary>
    /// When the rep's authority ends by design (e.g. a time-bounded POA).
    /// Distinct from <see cref="ExpiresAt"/> — EffectiveTo is a planned end,
    /// ExpiresAt is a document's notarized expiration.
    /// </summary>
    public DateTime? EffectiveTo { get; set; }

    /// <summary>
    /// Notarized expiration date from the proof-of-authority document
    /// itself. Drives the read-time <see cref="ObservedStatus"/> projection
    /// onto <see cref="PersonalRepStatus.Inactive"/>.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// UUID pointer into member-document-service where the proof-of-authority
    /// document (POA, guardianship order, notarized affidavit) is stored.
    /// Not PHI in isolation — safe to index and emit on events.
    /// </summary>
    [StringLength(100)]
    public string? ProofOfAuthorityDocumentId { get; set; }

    // ── Encrypted at rest — never included in Kafka event payload ───────

    /// <summary>Encrypted at rest via <see cref="Services.IPersonalRepFieldEncryptor"/>.</summary>
    [StringLength(500)]
    public string? FirstName { get; set; }

    /// <summary>Encrypted at rest.</summary>
    [StringLength(500)]
    public string? MiddleName { get; set; }

    /// <summary>Encrypted at rest.</summary>
    [StringLength(500)]
    public string? LastName { get; set; }

    /// <summary>Encrypted at rest.</summary>
    [StringLength(500)]
    public string? Email { get; set; }

    /// <summary>Encrypted at rest.</summary>
    [StringLength(100)]
    public string? PhoneNumber { get; set; }

    /// <summary>Encrypted at rest.</summary>
    [StringLength(500)]
    public string? MailingAddressLine1 { get; set; }

    /// <summary>Encrypted at rest.</summary>
    [StringLength(500)]
    public string? MailingAddressLine2 { get; set; }

    /// <summary>Encrypted at rest.</summary>
    [StringLength(200)]
    public string? MailingAddressCity { get; set; }

    /// <summary>Encrypted at rest.</summary>
    [StringLength(50)]
    public string? MailingAddressStateCode { get; set; }

    /// <summary>Encrypted at rest.</summary>
    [StringLength(20)]
    public string? MailingAddressPostalCode { get; set; }

    /// <summary>Free-text relationship notes. Encrypted at rest.</summary>
    [StringLength(4000)]
    public string? RelationshipNotes { get; set; }

    // ── Lifecycle timestamps ────────────────────────────────────────────

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [StringLength(200)]
    public string? UpdatedBy { get; set; }

    [StringLength(200)]
    public string? ActivatedBy { get; set; }
    public DateTime? ActivatedAt { get; set; }

    [StringLength(200)]
    public string? InactivatedBy { get; set; }
    public DateTime? InactivatedAt { get; set; }

    /// <summary>
    /// Controlled reason code for inactivation. Safe to include in event
    /// payload (enum-valued). See <see cref="PersonalRepInactivationReasonCode"/>.
    /// </summary>
    public PersonalRepInactivationReasonCode? InactivationReasonCode { get; set; }

    // ── Soft-delete (rare; admin-only; reserved for data-entry errors) ───

    public DateTime? DeletedAt { get; set; }

    [StringLength(200)]
    public string? DeletedBy { get; set; }

    [StringLength(500)]
    public string? DeletedReason { get; set; }

    [BsonIgnore]
    public bool IsDeleted => DeletedAt.HasValue;

    /// <summary>
    /// Projects the persisted status into the status the caller observes —
    /// <see cref="PersonalRepStatus.Inactive"/> whenever the record is still
    /// persisted as <see cref="PersonalRepStatus.Active"/> but the effective
    /// <see cref="ExpiresAt"/> has passed. The observed-transition write to
    /// the persisted status happens in
    /// <see cref="Repositories.IPersonalRepRepository.TryTransitionToInactiveAsync"/>.
    /// </summary>
    public PersonalRepStatus ObservedStatus(DateTime? asOf = null)
    {
        var t = asOf ?? DateTime.UtcNow;
        if (Status == PersonalRepStatus.Active && ExpiresAt.HasValue && ExpiresAt.Value <= t)
            return PersonalRepStatus.Inactive;
        return Status;
    }
}

/// <summary>
/// Personal Representative lifecycle state. See
/// <c>Services.PersonalRepStateMachine</c> for allowed transitions;
/// <see cref="Inactive"/> is a read-time projection of <see cref="Active"/>
/// once <see cref="PersonalRepresentative.ExpiresAt"/> passes.
/// </summary>
public enum PersonalRepStatus
{
    Draft = 1,
    Active = 2,
    Inactive = 3
}

/// <summary>
/// The credential by which the rep claims authority. Legal review
/// questions flagged on the PR for the ambiguous values
/// (<see cref="HealthcareSurrogate"/> vs "healthcare agent" in some
/// state statutes; <see cref="Conservator"/> vs <see cref="LegalGuardian"/>
/// overlap in Texas Medicaid).
///
/// Minor-consent scenarios (Tex. Fam. Code §32.003 — a minor consenting
/// without a representative) are NOT modeled here; that is application-
/// layer logic belonging in consent-service.
/// </summary>
public enum PersonalRepCredentialType
{
    /// <summary>Biological / adoptive parent of a minor member.</summary>
    // TODO(minor-consent-interaction): §32.003 minor-consent logic lives
    // in consent-service / clinical workflows, not here.
    Parent = 1,

    /// <summary>Court-appointed legal guardian.</summary>
    LegalGuardian = 2,

    /// <summary>Holder of a healthcare power of attorney.</summary>
    HealthcarePowerOfAttorney = 3,

    /// <summary>
    /// State-statute-designated healthcare surrogate. Naming varies by
    /// jurisdiction ("agent" in some states); unified under "surrogate"
    /// pending legal review.
    /// </summary>
    HealthcareSurrogate = 4,

    /// <summary>
    /// Court-appointed conservator. In most state probate law, conservators
    /// have financial-decision authority distinct from a
    /// <see cref="LegalGuardian"/>'s personal/healthcare authority. Legal
    /// review on the PR confirms whether Texas Medicaid treats this as
    /// equivalent to LegalGuardian.
    /// </summary>
    Conservator = 5,

    /// <summary>Escape hatch for any credential type legal flags later.</summary>
    Other = 99
}

/// <summary>
/// Controlled inactivation reasons. Safe to include in event payloads
/// (no PHI). Unlike consent-service which models Expired as a distinct
/// terminal status, personal-rep collapses all termination reasons into
/// Inactive with a discriminator here — see
/// <c>Services.PersonalRepStateMachine</c> remarks.
/// </summary>
public enum PersonalRepInactivationReasonCode
{
    RepDeceased = 1,
    PoaRevoked = 2,
    GuardianshipEnded = 3,
    Expired = 4,
    AdminError = 5,
    Other = 99
}

/// <summary>
/// Thrown when a caller attempts a Personal Representative lifecycle
/// transition that is not allowed by <c>Services.PersonalRepStateMachine</c>.
/// Distinct from a generic <see cref="InvalidOperationException"/> so the
/// controller layer can map it to a 409 Conflict with
/// <see cref="FromStatus"/>/<see cref="ToStatus"/> in ProblemDetails rather
/// than a 500.
/// </summary>
public sealed class InvalidPersonalRepTransitionException : InvalidOperationException
{
    public PersonalRepStatus FromStatus { get; }
    public PersonalRepStatus ToStatus { get; }

    public InvalidPersonalRepTransitionException(PersonalRepStatus from, PersonalRepStatus to)
        : base($"Personal Representative transition {from} -> {to} is not allowed.")
    {
        FromStatus = from;
        ToStatus = to;
    }
}
