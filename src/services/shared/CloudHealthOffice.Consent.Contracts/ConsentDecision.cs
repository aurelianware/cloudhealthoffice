namespace CloudHealthOffice.Consent.Contracts;

/// <summary>
/// Why an authorization was allowed or refused. A closed set rather than a
/// message, so audit rows and API responses branch on the same values and an
/// operator asking "why was this exchange allowed?" gets an answer that is
/// stable across releases and carries no PHI.
/// </summary>
public enum ConsentAuthorizationReason
{
    /// <summary>An active, in-force consent for the requested purpose was found.</summary>
    Granted,

    /// <summary>The member has no consent at all on record in this tenant.</summary>
    NoConsentOnRecord,

    /// <summary>
    /// The member has consents, but none for the requested purpose. This is the
    /// reason a Provider Access consent does not open a Payer-to-Payer exchange.
    /// </summary>
    NoConsentForPurpose,

    /// <summary>A consent for the purpose exists but was never activated (still Draft).</summary>
    NotActivated,

    /// <summary>A consent for the purpose exists and was revoked.</summary>
    Revoked,

    /// <summary>A consent for the purpose exists but its effective period has ended.</summary>
    Expired,

    /// <summary>A consent for the purpose exists but does not take effect until later.</summary>
    NotYetEffective,
}

/// <summary>
/// The authorization decision, with the evidence needed to justify it later: the
/// purpose asked about, the consent that answered (when one did), the reason,
/// and the instant the decision was evaluated at.
///
/// A decision is a point-in-time answer. It is not a licence held open: a
/// long-running exchange re-evaluates rather than carrying an old
/// <see cref="Allowed"/> forward.
/// </summary>
public sealed record ConsentDecision
{
    public required bool Allowed { get; init; }
    public required ConsentAuthorizationReason Reason { get; init; }
    public required ConsentPurposeOfUse PurposeOfUse { get; init; }

    /// <summary>The consent that authorized this, when one did. Opaque id only.</summary>
    public string? ConsentId { get; init; }

    /// <summary>Version of that consent, when the registry tracks one.</summary>
    public long? ConsentVersion { get; init; }

    /// <summary>The instant the decision was evaluated as of.</summary>
    public required DateTime EvaluatedAtUtc { get; init; }

    public static ConsentDecision Grant(
        ConsentPurposeOfUse purpose, ConsentAuthorizationSnapshot consent, DateTime evaluatedAtUtc) =>
        new()
        {
            Allowed = true,
            Reason = ConsentAuthorizationReason.Granted,
            PurposeOfUse = purpose,
            ConsentId = consent.ConsentId,
            ConsentVersion = consent.Version,
            EvaluatedAtUtc = evaluatedAtUtc,
        };

    public static ConsentDecision Deny(
        ConsentPurposeOfUse purpose,
        ConsentAuthorizationReason reason,
        DateTime evaluatedAtUtc,
        string? consentId = null) =>
        new()
        {
            Allowed = false,
            Reason = reason,
            PurposeOfUse = purpose,
            ConsentId = consentId,
            EvaluatedAtUtc = evaluatedAtUtc,
        };
}
