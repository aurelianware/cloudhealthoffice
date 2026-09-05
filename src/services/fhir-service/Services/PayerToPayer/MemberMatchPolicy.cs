using FhirService.Models;
using FhirService.Models.PayerToPayer;

namespace FhirService.Services.PayerToPayer;

/// <summary>How strongly a single candidate matches the supplied criteria.</summary>
public enum MemberMatchStrength
{
    /// <summary>No positive basis to assert this is the member.</summary>
    None,

    /// <summary>Some attributes agree, but not enough to assert identity on their own.</summary>
    Supporting,

    /// <summary>A strong identifier, or the family-name + birth-date pair, agrees with no contradiction.</summary>
    Strong,

    /// <summary>A supplied identity attribute contradicts this candidate — it is not the member.</summary>
    Conflict,
}

/// <summary>
/// Deterministic, fail-safe evaluation of a single candidate against normalized
/// member-match criteria (P2P-04). No probabilistic scoring: every supplied
/// attribute the candidate can be compared on must agree, and a positive
/// assertion needs a strong identifier (member/subscriber id or SSN) or the
/// family-name + birth-date pair. Any contradiction — a wrong DOB, a wrong
/// member id, a different sex — makes the candidate a <see cref="MemberMatchStrength.Conflict"/>,
/// never a match. Only <see cref="MemberMatchStrength.Strong"/> candidates are
/// eligible; the caller refuses when more than one is Strong.
/// </summary>
public static class MemberMatchPolicy
{
    public static MemberMatchStrength Evaluate(
        MemberMatchCriteria criteria, ChoMember member, IReadOnlyList<ChoCoverage> coverages)
    {
        // The identifiers this member is known by: the CHO member id plus any
        // subscriber id on the member's coverages (the id the member held under a
        // prior payer legitimately resolves to this member).
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedMemberId = MemberIdentityNormalizer.Identifier(member.MemberId);
        if (normalizedMemberId is not null) candidateIds.Add(normalizedMemberId);
        foreach (var coverage in coverages)
        {
            var sub = MemberIdentityNormalizer.Identifier(coverage.SubscriberId);
            if (sub is not null) candidateIds.Add(sub);
        }

        // ── Hard contradictions exclude the candidate outright ───────────────────
        // A strong identifier the caller asserted that this member does not bear.
        if (criteria.MemberId is not null && !candidateIds.Contains(criteria.MemberId))
            return MemberMatchStrength.Conflict;

        if (Contradicts(criteria.Ssn, MemberIdentityNormalizer.Identifier(member.Ssn)))
            return MemberMatchStrength.Conflict;
        if (Contradicts(criteria.FamilyName, MemberIdentityNormalizer.Name(member.LastName)))
            return MemberMatchStrength.Conflict;
        if (Contradicts(criteria.BirthDate, MemberIdentityNormalizer.BirthDate(member.Dob)))
            return MemberMatchStrength.Conflict;
        if (Contradicts(criteria.GivenName, MemberIdentityNormalizer.Name(member.FirstName)))
            return MemberMatchStrength.Conflict;
        if (Contradicts(criteria.Gender, MemberIdentityNormalizer.Gender(member.Gender)))
            return MemberMatchStrength.Conflict;
        if (Contradicts(criteria.PostalCode, MemberIdentityNormalizer.PostalCode(member.Address?.Zip)))
            return MemberMatchStrength.Conflict;
        if (Contradicts(criteria.Phone, MemberIdentityNormalizer.Phone(member.Phone)))
            return MemberMatchStrength.Conflict;
        if (Contradicts(criteria.Email, MemberIdentityNormalizer.Email(member.Email)))
            return MemberMatchStrength.Conflict;

        // ── Positive basis ───────────────────────────────────────────────────────
        var strongIdentifier =
            (criteria.MemberId is not null && candidateIds.Contains(criteria.MemberId))
            || Agrees(criteria.Ssn, MemberIdentityNormalizer.Identifier(member.Ssn));

        var demographicPair =
            Agrees(criteria.FamilyName, MemberIdentityNormalizer.Name(member.LastName))
            && Agrees(criteria.BirthDate, MemberIdentityNormalizer.BirthDate(member.Dob));

        if (strongIdentifier || demographicPair)
            return MemberMatchStrength.Strong;

        // No contradiction, but nothing strong enough to assert identity (e.g. only
        // a gender or postal code agreed). Never treated as a match.
        return MemberMatchStrength.Supporting;
    }

    /// <summary>
    /// True when both values are supplied and differ. A value the member does not
    /// carry (null) is "unknown", not a contradiction.
    /// </summary>
    private static bool Contradicts(string? criteriaValue, string? memberValue)
        => criteriaValue is not null && memberValue is not null
           && !string.Equals(criteriaValue, memberValue, StringComparison.Ordinal);

    /// <summary>True when both values are supplied and equal.</summary>
    private static bool Agrees(string? criteriaValue, string? memberValue)
        => criteriaValue is not null && memberValue is not null
           && string.Equals(criteriaValue, memberValue, StringComparison.Ordinal);
}
