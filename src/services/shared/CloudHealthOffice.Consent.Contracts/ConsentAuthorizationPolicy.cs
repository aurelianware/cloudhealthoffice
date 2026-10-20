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
/// operation; ties break by highest version, then by consent id. The id
/// tie-break is what makes the choice reproducible: without it two equally
/// valid consents would resolve by whatever order the source happened to
/// return, so the same registry state could name different authorizations on
/// two evaluations. The same ordering picks which near miss a refusal names.
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
            .Where(c => c.Provision == ConsentProvision.Permit
                        && c.Status == ConsentLifecycleStatus.Active && IsInForce(c, asOfUtc))
            // Latest-expiring first, unbounded ahead of bounded, then highest
            // version, then consent id — the id is the stable tie-break that
            // keeps the selection independent of the source's ordering.
            .OrderByDescending(c => c.ExpiresAt ?? DateTime.MaxValue)
            .ThenByDescending(c => c.Version ?? 0)
            .ThenBy(c => c.ConsentId, StringComparer.Ordinal)
            .FirstOrDefault();

        // An in-force opt-out refuses the purpose whatever permits exist.
        if (InForceOptOut(forPurpose, asOfUtc) is { } optOut)
            return ConsentDecision.Deny(purpose, ConsentAuthorizationReason.OptedOut, asOfUtc, optOut.ConsentId);

        if (inForce is not null)
            return ConsentDecision.Grant(purpose, inForce, asOfUtc);

        // Nothing in force: say which of the near misses it was, most specific
        // first, so the audit trail distinguishes "they said no" from "it lapsed"
        // from "it has not started yet". Ordered by consent id so that when
        // several records fall in the same category the refusal names the same
        // one every time, whatever order the source returned them in.
        var candidates = forPurpose
            .Where(c => c.Provision == ConsentProvision.Permit)
            .OrderBy(c => c.ConsentId, StringComparer.Ordinal)
            .ToList();
        // Only lapsed or revoked opt-outs on record: nothing ever permitted it.
        if (candidates.Count == 0)
            return ConsentDecision.Deny(purpose, ConsentAuthorizationReason.NoConsentForPurpose, asOfUtc);

        var revoked = candidates.FirstOrDefault(c => c.Status == ConsentLifecycleStatus.Revoked);
        if (revoked is not null)
            return ConsentDecision.Deny(
                purpose, ConsentAuthorizationReason.Revoked, asOfUtc, revoked.ConsentId);

        var expired = candidates.FirstOrDefault(c =>
            c.Status == ConsentLifecycleStatus.Expired
            || (c.Status == ConsentLifecycleStatus.Active && HasLapsed(c, asOfUtc)));
        if (expired is not null)
            return ConsentDecision.Deny(
                purpose, ConsentAuthorizationReason.Expired, asOfUtc, expired.ConsentId);

        var pending = candidates.FirstOrDefault(c =>
            c.Status == ConsentLifecycleStatus.Active && NotYetEffective(c, asOfUtc));
        if (pending is not null)
            return ConsentDecision.Deny(
                purpose, ConsentAuthorizationReason.NotYetEffective, asOfUtc, pending.ConsentId);

        return ConsentDecision.Deny(
            purpose, ConsentAuthorizationReason.NotActivated, asOfUtc, candidates[0].ConsentId);
    }

    /// <summary>
    /// Decides a purpose that is allowed unless the member opts out (CMS-0057-F
    /// Provider Access). Same tenant, member and purpose rules as
    /// <see cref="Evaluate"/>; then, in order:
    ///   * an in-force opt-out (Active <see cref="ConsentProvision.Deny"/>)
    ///     refuses (<see cref="ConsentAuthorizationReason.OptedOut"/>);
    ///   * an in-force permit allows and is named as the authorization;
    ///   * a revoked permit with no permit in force refuses
    ///     (<see cref="ConsentAuthorizationReason.Revoked"/>): under the opt-in
    ///     model that preceded this one, revoking was how a member said no, and
    ///     that refusal is honoured rather than silently reversed;
    ///   * otherwise allows (<see cref="ConsentAuthorizationReason.NotOptedOut"/>).
    /// A lapsed or revoked opt-out no longer refuses. The caller supplies only
    /// records it actually read: a registry it could not read must be refused
    /// before this (<see cref="ConsentAuthorizationReason.RegistryUnavailable"/>),
    /// because no records here means "never opted out".
    /// </summary>
    public static ConsentDecision EvaluateOptOut(
        string tenantId,
        string memberId,
        ConsentPurposeOfUse purpose,
        IReadOnlyList<ConsentAuthorizationSnapshot> consents,
        DateTime asOfUtc)
    {
        if (purpose == ConsentPurposeOfUse.Unspecified
            || string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(memberId))
            return ConsentDecision.Deny(purpose, ConsentAuthorizationReason.NoConsentForPurpose, asOfUtc);

        var forPurpose = consents
            .Where(c => string.Equals(c.TenantId, tenantId, StringComparison.Ordinal)
                     && string.Equals(c.MemberId, memberId, StringComparison.Ordinal)
                     && c.PurposeOfUse == purpose)
            .ToList();

        if (InForceOptOut(forPurpose, asOfUtc) is { } optOut)
            return ConsentDecision.Deny(purpose, ConsentAuthorizationReason.OptedOut, asOfUtc, optOut.ConsentId);

        var permit = forPurpose
            .Where(c => c.Provision == ConsentProvision.Permit
                        && c.Status == ConsentLifecycleStatus.Active && IsInForce(c, asOfUtc))
            .OrderByDescending(c => c.ExpiresAt ?? DateTime.MaxValue)
            .ThenByDescending(c => c.Version ?? 0)
            .ThenBy(c => c.ConsentId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (permit is not null)
            return ConsentDecision.Grant(purpose, permit, asOfUtc);

        var revoked = forPurpose
            .Where(c => c.Provision == ConsentProvision.Permit && c.Status == ConsentLifecycleStatus.Revoked)
            .OrderBy(c => c.ConsentId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (revoked is not null)
            return ConsentDecision.Deny(purpose, ConsentAuthorizationReason.Revoked, asOfUtc, revoked.ConsentId);

        return new ConsentDecision
        {
            Allowed = true,
            Reason = ConsentAuthorizationReason.NotOptedOut,
            PurposeOfUse = purpose,
            EvaluatedAtUtc = asOfUtc,
        };
    }

    private static ConsentAuthorizationSnapshot? InForceOptOut(
        IEnumerable<ConsentAuthorizationSnapshot> forPurpose, DateTime asOfUtc) =>
        forPurpose
            .Where(c => c.Provision == ConsentProvision.Deny
                        && c.Status == ConsentLifecycleStatus.Active && IsInForce(c, asOfUtc))
            .OrderBy(c => c.ConsentId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool IsInForce(ConsentAuthorizationSnapshot consent, DateTime asOfUtc)
        => !NotYetEffective(consent, asOfUtc) && !HasLapsed(consent, asOfUtc);

    private static bool NotYetEffective(ConsentAuthorizationSnapshot consent, DateTime asOfUtc)
        => consent.EffectiveAt.HasValue && asOfUtc < consent.EffectiveAt.Value;

    private static bool HasLapsed(ConsentAuthorizationSnapshot consent, DateTime asOfUtc)
        => consent.ExpiresAt.HasValue && asOfUtc >= consent.ExpiresAt.Value;
}
