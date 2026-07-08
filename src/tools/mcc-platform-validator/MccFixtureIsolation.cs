using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal static class MccFixtureIsolation
{
    public static void IsolateCobPendMembers(IEnumerable<SyntheticClaim> claims, int seed)
    {
        var ordinal = 0;
        foreach (var claim in claims.Where(IsSupportedCobPendScenario))
        {
            ordinal++;
            var isolatedId = BuildCobPendMemberId(claim.ClaimId, seed, ordinal);
            claim.Member.MemberId = isolatedId;
            claim.Member.SubscriberId = isolatedId;

            foreach (var coverage in claim.Member.Coverages)
            {
                coverage.MemberId = isolatedId;
                coverage.SubscriberId = isolatedId;
            }

            foreach (var dependent in claim.Member.Dependents)
            {
                dependent.SubscriberMemberId = isolatedId;
                dependent.SubscriberId = isolatedId;

                foreach (var coverage in dependent.Coverages)
                {
                    coverage.SubscriberId = isolatedId;
                }
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
