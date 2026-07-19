using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

public static class MccValidationProviderNormalizer
{
    public static int Normalize(
        IReadOnlyCollection<SyntheticClaim> claims,
        int seed,
        Guid runId,
        MccWorkflowValidationCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var scenarioIndex = 0;
        var normalized = 0;
        foreach (var claim in claims.OrderBy(c => c.ClaimId, StringComparer.Ordinal))
        {
            var expected = MccWorkflowValidation.ExpectedValidationFor(claim, capabilities);
            if (expected.ExpectedOutcome is null)
            {
                continue;
            }

            MccValidationProviderProfile.ForceAdjudicatable(claim.BillingProvider, claim.DateOfService);
            claim.BillingProvider.Npi = MccValidationProviderIdentity.BuildNpi(seed, runId, scenarioIndex, role: 0);

            if (!string.Equals(
                    expected.ExpectedBusinessDenialCode,
                    MccWorkflowValidation.ProviderExcludedCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                MccValidationProviderProfile.ForceAdjudicatable(claim.RenderingProvider, claim.DateOfService);
                claim.RenderingProvider.Npi = MccValidationProviderIdentity.BuildNpi(seed, runId, scenarioIndex, role: 1);
            }
            else
            {
                claim.RenderingProvider.Npi = MccValidationProviderIdentity.BuildNpi(seed, runId, scenarioIndex, role: 2);
            }

            scenarioIndex++;
            normalized++;
        }

        return normalized;
    }
}
