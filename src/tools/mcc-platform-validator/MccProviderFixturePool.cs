using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

public sealed record MccProviderFixturePoolResult(
    int ProvidersBefore,
    int ProvidersAfter,
    int ReusedAssignments,
    int ProtectedClaims);

/// <summary>
/// Reuses provider fixtures when every adjudication-relevant profile field is
/// equivalent. Provider-sensitive prior-authorization scenarios, and claims
/// whose provider identity was deliberately forced (e.g. the excluded-provider
/// scenario), retain their original identities.
/// </summary>
public static class MccProviderFixturePool
{
    public static MccProviderFixturePoolResult Apply(
        IReadOnlyCollection<SyntheticClaim> claims,
        int seed,
        Guid runId)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var providersBefore = DistinctProviderCount(claims);
        var pool = new Dictionary<ProviderProfileKey, SyntheticProvider>();
        var reusedAssignments = 0;
        var fixtureIndex = 0;
        var protectedClaims = 0;

        foreach (var claim in claims.OrderBy(claim => claim.ClaimId, StringComparer.Ordinal))
        {
            if (RequiresProviderIdentityIsolation(claim))
            {
                protectedClaims++;
                continue;
            }

            claim.BillingProvider = Reuse(
                pool, claim.BillingProvider, "billing", seed, runId, ref fixtureIndex, ref reusedAssignments);
            claim.RenderingProvider = Reuse(
                pool, claim.RenderingProvider, "rendering", seed, runId, ref fixtureIndex, ref reusedAssignments);
        }

        return new MccProviderFixturePoolResult(
            providersBefore,
            DistinctProviderCount(claims),
            reusedAssignments,
            protectedClaims);
    }

    private static SyntheticProvider Reuse(
        IDictionary<ProviderProfileKey, SyntheticProvider> pool,
        SyntheticProvider provider,
        string role,
        int seed,
        Guid runId,
        ref int fixtureIndex,
        ref int reusedAssignments)
    {
        var key = ProviderProfileKey.From(provider, role);
        if (pool.TryGetValue(key, out var pooled))
        {
            reusedAssignments++;
            return pooled;
        }

        // Use a run-scoped identity so an older tenant fixture cannot carry
        // credentialing or network state into this run's canonical profile.
        provider.Npi = BuildRunScopedNpi(seed, runId, fixtureIndex++);
        pool[key] = provider;
        return provider;
    }

    private static string BuildRunScopedNpi(int seed, Guid runId, int index)
    {
        unchecked
        {
            var runHash = BitConverter.ToUInt32(runId.ToByteArray(), 4);
            var combined = runHash ^ (uint)(seed * 1_000_003 + index * 97);
            var value = combined % 1_000_000;
            var baseNineDigits = $"92{index % 10}{value:D6}";
            return $"{baseNineDigits}{CalculateNpiCheckDigit(baseNineDigits)}";
        }
    }

    private static int CalculateNpiCheckDigit(string baseNineDigits)
    {
        var candidate = $"80840{baseNineDigits}0";
        var sum = 0;
        var doubleDigit = false;
        for (var i = candidate.Length - 1; i >= 0; i--)
        {
            var digit = candidate[i] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleDigit = !doubleDigit;
        }

        return (10 - (sum % 10)) % 10;
    }

    private static bool RequiresProviderIdentityIsolation(SyntheticClaim claim)
        => claim.PriorAuthStatus.Equals("Required", StringComparison.OrdinalIgnoreCase)
           || claim.EdgeCase is EdgeCaseScenario.PriorAuthRequired_AuthOnFile
               or EdgeCaseScenario.PriorAuthRequired_NoAuth
               or EdgeCaseScenario.PriorAuthRequired_ExpiredAuth
               or EdgeCaseScenario.PriorAuthRequired_WrongProvider
               or EdgeCaseScenario.PriorAuthRequired_WrongProcedure
           // InjectExcludedProviderScenarios (Program.cs) forces this claim's
           // rendering provider to a deliberately-excluded identity before
           // this pool runs. Without isolation, pooling can hand that same
           // excluded provider object to an unrelated claim whose profile
           // happens to match post-force (credentialing/network status are
           // both forced to "Excluded" and aren't otherwise distinctive at
           // scale) -- silently turning an unrelated scenario into a
           // provider-exclusion denial. Only shows up once claim volume is
           // large enough to pressure the pool into reusing this identity;
           // confirmed via a 50K run where two unrelated edge-case claims
           // (BehavioralHealthCarveOut, RetroEligibilityAdd) were denied
           // PROVIDER_EXCLUDED against a rendering NPI that resolved to a
           // seeded "Excluded ProviderNN" fixture.
           || string.Equals(claim.BenefitPlanId, MccWorkflowValidation.ExcludedProviderPlanId, StringComparison.Ordinal);

    private static int DistinctProviderCount(IEnumerable<SyntheticClaim> claims)
        => claims
            .SelectMany(claim => new[] { claim.BillingProvider.Npi, claim.RenderingProvider.Npi })
            .Where(npi => !string.IsNullOrWhiteSpace(npi))
            .Distinct(StringComparer.Ordinal)
            .Count();

    private sealed record ProviderProfileKey(
        string Role,
        string ProviderType,
        string SpecialtyCode,
        string TaxonomyCode,
        bool IsParticipating,
        string NetworkStatus,
        string CredentialingStatus,
        string State,
        string ContractType,
        string FeeScheduleId,
        DateTime EffectiveDate,
        DateTime? TermDate,
        bool AcceptingNewPatients)
    {
        public static ProviderProfileKey From(SyntheticProvider provider, string role) => new(
            role,
            provider.ProviderType,
            provider.SpecialtyCode,
            provider.TaxonomyCode,
            provider.IsParticipating,
            provider.NetworkStatus,
            provider.CredentialingStatus,
            provider.State,
            provider.ContractType,
            provider.FeeScheduleId ?? string.Empty,
            provider.EffectiveDate.Date,
            provider.TermDate?.Date,
            provider.AcceptingNewPatients);
    }
}
