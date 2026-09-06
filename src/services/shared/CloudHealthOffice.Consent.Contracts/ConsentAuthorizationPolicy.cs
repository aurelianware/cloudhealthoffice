namespace CloudHealthOffice.Consent.Contracts;

/// <summary>
/// Decides whether a member has authorized a given purpose, as of a given
/// instant. Pure: it holds no state, reads no store, and takes the candidate
/// consents from its caller.
///
/// It exists so there is ONE answer to "is this member authorized?" — the
/// inbound Payer-to-Payer respond and the outbound initiation evaluate the same
/// logic rather than each writing their own, which is how one direction ends up
/// quietly more permissive than the other.
///
/// The rules, in order, and all fail-closed:
///   * the consent must belong to the SAME tenant and the SAME member — a
///     snapshot for anyone else is not evidence about this member, whatever the
///     caller believes it fetched;
///   * it must carry the requested purpose. <see cref="ConsentPurposeOfUse.Unspecified"/>
///     never satisfies a purpose-specific check, so a consent written before the
///     purpose axis existed authorizes nothing here;
///   * it must be Active — Draft is not yet authorization, Revoked is the
///     member's refusal, and Expired is authorization that has lapsed;
///   * it must be in force at <c>asOfUtc</c>: not before <c>EffectiveAt</c>, not
///     after <c>ExpiresAt</c>. Expiry is applied here rather than trusted from
///     the persisted status, so a record that lapsed since it was last written
///     cannot authorize anything.
/// When several consents qualify, the one expiring latest (unbounded first)
/// wins, so the decision names the authorization that actually covers the
/// operation.
/// </summary>
public static class ConsentAuthorizationPolicy
{
    public static ConsentDecision Evaluate(
        string tenantId,
        string memberId,
        ConsentPurposeOfUse purpose,
        IReadOnlyList<ConsentAuthorizationSnapshot> consents,
        DateTime asOfUtc)
    {
        // A purpose-specific question can never be answered by "no purpose".
        if (purpose == ConsentPurposeOfUse.Unspecified)
            return ConsentDecision.Deny(purpose, ConsentAuthorizationReason.NoConsentForPurpose, asOfUtc);

        var mine = consents
            .Where(c => string.Equals(c.TenantId, tenantId, StringComparison.Ordinal)
                     && string.Equals(c.MemberId, memberId, StringComparison.Ordinal))
            .ToList();

        if (mine.Count == 0)
            return ConsentDecision.Deny(purpose, ConsentAuthorizationReason.NoConsentOnRecord, asOfUtc);

        var forPurpose = mine.Where(c => c.PurposeOfUse == purpose).ToList();
        if (forPurpose.Count == 0)
            return ConsentDecision.Deny(purpose, ConsentAuthorizationReason.NoConsentForPurpose, asOfUtc);

        var inForce = forPurpose
            .Where(c => c.Status == ConsentLifecycleStatus.Active && IsInForce(c, asOfUtc))
            // Latest-expiring first, unbounded ahead of bounded.
            .OrderByDescending(c => c.ExpiresAt ?? DateTime.MaxValue)
            .ThenByDescending(c => c.Version ?? 0)
            .FirstOrDefault();

        if (inForce is not null)
            return ConsentDecision.Grant(purpose, inForce, asOfUtc);

        // Nothing in force: say which of the near misses it was, most specific
        // first, so the audit trail distinguishes "they said no" from "it lapsed"
        // from "it has not started yet".
        var revoked = forPurpose.FirstOrDefault(c => c.Status == ConsentLifecycleStatus.Revoked);
        if (revoked is not null)
            return ConsentDecision.Deny(
                purpose, ConsentAuthorizationReason.Revoked, asOfUtc, revoked.ConsentId);

        var expired = forPurpose.FirstOrDefault(c =>
            c.Status == ConsentLifecycleStatus.Expired
            || (c.Status == ConsentLifecycleStatus.Active && HasLapsed(c, asOfUtc)));
        if (expired is not null)
            return ConsentDecision.Deny(
                purpose, ConsentAuthorizationReason.Expired, asOfUtc, expired.ConsentId);

        var pending = forPurpose.FirstOrDefault(c =>
            c.Status == ConsentLifecycleStatus.Active && NotYetEffective(c, asOfUtc));
        if (pending is not null)
            return ConsentDecision.Deny(
                purpose, ConsentAuthorizationReason.NotYetEffective, asOfUtc, pending.ConsentId);

        return ConsentDecision.Deny(
            purpose, ConsentAuthorizationReason.NotActivated, asOfUtc, forPurpose[0].ConsentId);
    }

    private static bool IsInForce(ConsentAuthorizationSnapshot consent, DateTime asOfUtc)
        => !NotYetEffective(consent, asOfUtc) && !HasLapsed(consent, asOfUtc);

    private static bool NotYetEffective(ConsentAuthorizationSnapshot consent, DateTime asOfUtc)
        => consent.EffectiveAt.HasValue && asOfUtc < consent.EffectiveAt.Value;

    private static bool HasLapsed(ConsentAuthorizationSnapshot consent, DateTime asOfUtc)
        => consent.ExpiresAt.HasValue && asOfUtc >= consent.ExpiresAt.Value;
}
