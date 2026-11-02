using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal static class MccFixtureIsolation
{
    public static void IsolateValidationMembers(IEnumerable<SyntheticClaim> claims, int seed, Guid runId)
    {
        var ordinal = 0;
        foreach (var claim in claims.OrderBy(c => c.ClaimId, StringComparer.Ordinal))
        {
            var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
            if (expected.ExpectedOutcome is null)
            {
                continue;
            }

            ordinal++;
            if (IsSupportedCobPendScenario(claim))
            {
                RewriteMemberIdentity(claim, BuildCobPendMemberId(claim.ClaimId, seed, ordinal));
                continue;
            }

            var isolatedId = BuildValidationMemberId(claim.ClaimId, runId, ordinal);
            RewriteMemberIdentity(claim, isolatedId, SubscriberIdFor(claim.Member, isolatedId));
        }
    }

    /// <summary>
    /// A dependent (spouse, child) keeps a subscriber id of its own. Giving it
    /// its own member id as the subscriber id is what an EDI import that lost
    /// the dependent looks like, and claims adjudication refuses to bind such
    /// a claim to prior authorizations (it pends PAUNAVAIL, "dependent member
    /// identity is unresolved"). The first GKE run pended all 47 PA-scored
    /// claims that way.
    /// </summary>
    internal static string SubscriberIdFor(SyntheticMember member, string isolatedId)
        => string.Equals(MccClaimRelationship.X12Code(member), MccClaimRelationship.SelfCode, StringComparison.Ordinal)
            ? isolatedId
            : $"{isolatedId}S";

    public static void IsolateCobPendMembers(IEnumerable<SyntheticClaim> claims, int seed)
    {
        var ordinal = 0;
        foreach (var claim in claims.Where(IsSupportedCobPendScenario))
        {
            ordinal++;
            var isolatedId = BuildCobPendMemberId(claim.ClaimId, seed, ordinal);
            RewriteMemberIdentity(claim, isolatedId);
        }
    }

    private static void RewriteMemberIdentity(SyntheticClaim claim, string isolatedId, string? subscriberId = null)
    {
        subscriberId ??= isolatedId;
        claim.Member.MemberId = isolatedId;
        claim.Member.SubscriberId = subscriberId;

        foreach (var coverage in claim.Member.Coverages)
        {
            coverage.MemberId = isolatedId;
            coverage.SubscriberId = subscriberId;
        }

        foreach (var dependent in claim.Member.Dependents)
        {
            dependent.SubscriberMemberId = isolatedId;
            dependent.SubscriberId = subscriberId;

            foreach (var coverage in dependent.Coverages)
            {
                coverage.SubscriberId = subscriberId;
            }
        }
    }

    private static bool IsSupportedCobPendScenario(SyntheticClaim claim)
        => claim.EdgeCase is
            EdgeCaseScenario.CobSecondaryPayer or
            EdgeCaseScenario.CobTertiaryPayer or
            EdgeCaseScenario.CobBirthdayRule or
            EdgeCaseScenario.CobGenderRule or
            EdgeCaseScenario.MedicaidDualEligible;

    private static string BuildCobPendMemberId(string claimId, int seed, int ordinal)
    {
        var normalizedSeed = Math.Abs(seed % 100).ToString("D2");
        var normalizedClaimId = NormalizeClaimIdSuffix(claimId, ordinal);

        return $"MCCCB{normalizedSeed}{normalizedClaimId}";
    }

    private static string BuildValidationMemberId(string claimId, Guid runId, int ordinal)
    {
        var runSalt = runId.ToString("N")[..8].ToUpperInvariant();
        var claimType = ClaimTypeDiscriminator(claimId);
        var normalizedClaimId = NormalizeClaimIdSuffix(claimId, ordinal);

        return $"MCCV{runSalt}{claimType}{normalizedClaimId}";
    }

    private static char ClaimTypeDiscriminator(string claimId)
    {
        var parts = claimId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[1].Length == 1 && char.IsLetter(parts[1][0])
            ? char.ToUpperInvariant(parts[1][0])
            : 'X';
    }

    private static string NormalizeClaimIdSuffix(string claimId, int ordinal)
    {
        var suffix = string.IsNullOrWhiteSpace(claimId)
            ? string.Empty
            : new string(claimId.Where(char.IsDigit).ToArray());

        if (string.IsNullOrWhiteSpace(suffix))
        {
            suffix = ordinal.ToString();
        }

        return suffix.Length <= 7
            ? suffix.PadLeft(7, '0')
            : suffix[^7..];
    }
}
