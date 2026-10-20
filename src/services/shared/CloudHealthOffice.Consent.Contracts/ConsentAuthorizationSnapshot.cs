namespace CloudHealthOffice.Consent.Contracts;

/// <summary>
/// The minimal projection of a consent record needed to decide an
/// authorization: who it belongs to, what it authorizes, and when it is in
/// force. Deliberately carries NO free-text and no PHI-adjacent fields — the
/// grantor's name, the reason, the party the authorization names, and the
/// narrative purpose all stay encrypted on the consent aggregate in
/// consent-service and never cross a service boundary to answer "may we?".
/// </summary>
public sealed record ConsentAuthorizationSnapshot
{
    /// <summary>Tenant the consent belongs to. Compared, never assumed.</summary>
    public required string TenantId { get; init; }

    /// <summary>Member the consent belongs to. Compared, never assumed.</summary>
    public required string MemberId { get; init; }

    /// <summary>Opaque consent id — the evidence handle for "what authorized this?".</summary>
    public required string ConsentId { get; init; }

    public ConsentPurposeOfUse PurposeOfUse { get; init; } = ConsentPurposeOfUse.Unspecified;

    /// <summary>Whether the record permits or refuses its purpose; an opt-out is <see cref="ConsentProvision.Deny"/>.</summary>
    public ConsentProvision Provision { get; init; } = ConsentProvision.Permit;

    /// <summary>
    /// Persisted lifecycle status, as the registry holds it. Expiry is applied by
    /// the policy from <see cref="ExpiresAt"/>, so a record persisted Active but
    /// past its expiry is not treated as authorization.
    /// </summary>
    public ConsentLifecycleStatus Status { get; init; } = ConsentLifecycleStatus.Draft;

    /// <summary>When the record was activated, when known.</summary>
    public DateTime? ActivatedAt { get; init; }

    /// <summary>When the record was revoked, when known.</summary>
    public DateTime? RevokedAt { get; init; }

    /// <summary>
    /// For a revoked record: whether the revocation was the member's own choice
    /// (a member request, an unrecorded reason, or "other"), as opposed to an
    /// administrative correction, a supersession or an expiry. Only a member's
    /// revocation of a permit refuses an opt-out purpose. Defaults to true, so
    /// a registry that does not say is read as the member's refusal.
    /// </summary>
    public bool RevokedByMember { get; init; } = true;

    /// <summary>When the authorization takes effect; null means immediately.</summary>
    public DateTime? EffectiveAt { get; init; }

    /// <summary>When it lapses; null means unbounded.</summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// Monotonic version of the consent record, when the registry tracks one.
    /// Lets a decision name the exact revision it was made against.
    /// </summary>
    public long? Version { get; init; }
}

/// <summary>
/// Consent lifecycle status as the authorization contract sees it. Mirrors
/// <c>ConsentService.Models.ConsentStatus</c> by name and value; a drift test in
/// ConsentService.Tests keeps the two aligned so a rename cannot silently change
/// what "Revoked" means to the service enforcing it.
/// </summary>
public enum ConsentLifecycleStatus
{
    Draft = 1,
    Active = 2,
    Revoked = 3,
    Expired = 4,
}
