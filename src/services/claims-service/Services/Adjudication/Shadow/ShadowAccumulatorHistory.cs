using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// What a member had already met toward the deductible and out-of-pocket
/// maximum when the core adjudicated a claim, by the core's own account: the
/// member's (and family's) earlier claims loaded from that core. Shadow
/// adjudication prices the claim from this instead of CHO's accumulator
/// store, which holds nothing for a plan that has not gone live, so a
/// mid-year claim meets the deductible where the core did.
/// </summary>
public interface IShadowAccumulatorHistory
{
    /// <summary>
    /// The accumulation to price <paramref name="context"/>'s claim from. Never
    /// null: an empty one still keeps the shadow run off CHO's store.
    /// </summary>
    Task<PriorAccumulation> BuildAsync(ClaimAdjudicationContext context, string subscriberId, CancellationToken ct);
}

public sealed class ShadowAccumulatorHistory(IClaimRepository claims) : IShadowAccumulatorHistory
{
    /// <summary>Most earlier claims read for one claim. A member with more in a year is rare; the oldest are kept.</summary>
    public const int MaxClaims = 1000;

    public async Task<PriorAccumulation> BuildAsync(
        ClaimAdjudicationContext context, string subscriberId, CancellationToken ct)
    {
        var claim = context.Claim;
        var year = claim.ServiceDateFrom.Year;
        var rows = await claims.FindCoreClaimsForAccumulationAsync(
                claim.MemberId,
                string.IsNullOrWhiteSpace(subscriberId) ? null : subscriberId,
                new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                MaxClaims,
                ct)
            .ConfigureAwait(false);
        return From(claim, rows);
    }

    /// <summary>
    /// The entries for <paramref name="claim"/> from the core claims in its
    /// year: those the core adjudicated before it (its own adjudication date
    /// comes from the stored copy among <paramref name="coreClaims"/>; without
    /// dates, those with an earlier service date), on the same benefit plan.
    /// </summary>
    internal static PriorAccumulation From(AdapterClaim claim, IReadOnlyList<Claim> coreClaims)
    {
        var self = coreClaims.FirstOrDefault(c => string.Equals(c.Id, claim.Id, StringComparison.Ordinal));
        var entries = new List<PriorAccumulationEntry>();
        foreach (var core in coreClaims)
        {
            if (string.Equals(core.Id, claim.Id, StringComparison.Ordinal)) continue;
            if (!SamePlan(core.BenefitPlanId, claim.BenefitPlanId)) continue;
            if (!Before(core, self, claim)) continue;
            if (core.AdjudicationResult is not { } result) continue;

            var deductible = Math.Max(0m, result.DeductibleAmount);
            var outOfPocket = Math.Max(0m, result.OopAppliedAmount ?? result.PatientResponsibility);
            if (deductible == 0m && outOfPocket == 0m) continue;

            entries.Add(new PriorAccumulationEntry
            {
                ServiceDate = DateOnly.FromDateTime(core.ServiceDateFrom),
                ThisMember = string.Equals(core.MemberId, claim.MemberId, StringComparison.Ordinal),
                NetworkTier = string.Equals(result.NetworkTier, nameof(NetworkTier.OutOfNetwork), StringComparison.OrdinalIgnoreCase)
                    ? NetworkTier.OutOfNetwork
                    : NetworkTier.InNetwork,
                Deductible = deductible,
                OutOfPocket = outOfPocket,
            });
        }
        return new PriorAccumulation { Entries = entries };
    }

    private static bool SamePlan(string? a, string? b)
        => string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)
           || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The core applied <paramref name="core"/>'s cost share before it
    /// adjudicated the claim: by adjudication date when both have one (same
    /// day: the earlier service date first), otherwise by service date.
    /// </summary>
    private static bool Before(Claim core, Claim? self, AdapterClaim claim)
    {
        if (core.AdjudicatedDate is { } coreDone && self?.AdjudicatedDate is { } selfDone)
        {
            if (coreDone.Date != selfDone.Date) return coreDone < selfDone;
            return core.ServiceDateFrom < claim.ServiceDateFrom;
        }
        return core.ServiceDateFrom < claim.ServiceDateFrom;
    }
}
