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
/// mid-year claim meets the deductible where the core did. A starting balance
/// the core exported (<see cref="ShadowAccumulatorBalance"/>) replaces the
/// claims it already covers.
/// </summary>
public interface IShadowAccumulatorHistory
{
    /// <summary>
    /// The accumulation to price <paramref name="context"/>'s claim from. Never
    /// null: an empty one still keeps the shadow run off CHO's store.
    /// </summary>
    Task<PriorAccumulation> BuildAsync(ClaimAdjudicationContext context, string subscriberId, CancellationToken ct);
}

public sealed class ShadowAccumulatorHistory(
    IClaimRepository claims, IShadowAccumulatorBalanceRepository? balances = null) : IShadowAccumulatorHistory
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
        var starting = balances is null
            ? []
            : await balances.ListForMemberAsync(context.TenantId, claim.MemberId, year, ct).ConfigureAwait(false);
        return From(claim, rows, starting);
    }

    /// <summary>
    /// The entries for <paramref name="claim"/>. From the core claims in its
    /// year: those the core adjudicated before it (its own adjudication date
    /// comes from the stored copy among <paramref name="coreClaims"/>; without
    /// dates, those with an earlier service date), on the same benefit plan.
    /// From <paramref name="startingBalances"/>: for each network tier, the
    /// member's latest balance dated before the claim, which then stands in for
    /// every core claim of that tier the core had adjudicated by its date.
    /// </summary>
    internal static PriorAccumulation From(
        AdapterClaim claim, IReadOnlyList<Claim> coreClaims, IReadOnlyList<ShadowAccumulatorBalance>? startingBalances = null)
    {
        var self = coreClaims.FirstOrDefault(c => string.Equals(c.Id, claim.Id, StringComparison.Ordinal));
        var pricedOn = (self?.AdjudicatedDate ?? claim.ServiceDateFrom).Date;
        var baseline = (startingBalances ?? [])
            .Where(b => string.Equals(b.MemberId, claim.MemberId, StringComparison.Ordinal)
                        && b.AsOfDate.Year == claim.ServiceDateFrom.Year
                        && b.AsOfDate.Date < pricedOn)
            .GroupBy(b => TierOf(b.NetworkTier))
            .ToDictionary(g => g.Key, g => g.MaxBy(b => b.AsOfDate)!);

        var entries = new List<PriorAccumulationEntry>();
        foreach (var (tier, balance) in baseline.OrderBy(b => b.Key))
        {
            var asOf = DateOnly.FromDateTime(balance.AsOfDate);
            Add(entries, asOf, true, tier, balance.IndividualDeductibleMet, balance.IndividualOutOfPocketMet);
            // The rest of the family: the family total less this member's own.
            Add(entries, asOf, false, tier,
                Math.Max(0m, (balance.FamilyDeductibleMet ?? 0m) - balance.IndividualDeductibleMet),
                Math.Max(0m, (balance.FamilyOutOfPocketMet ?? 0m) - balance.IndividualOutOfPocketMet));
        }

        foreach (var core in coreClaims)
        {
            if (string.Equals(core.Id, claim.Id, StringComparison.Ordinal)) continue;
            if (!SamePlan(core.BenefitPlanId, claim.BenefitPlanId)) continue;
            if (!Before(core, self, claim)) continue;
            if (core.AdjudicationResult is not { } result) continue;

            var tier = TierOf(result.NetworkTier);
            if (baseline.TryGetValue(tier, out var covering)
                && (core.AdjudicatedDate ?? core.ServiceDateFrom).Date <= covering.AsOfDate.Date)
                continue; // already in the starting balance

            Add(entries, DateOnly.FromDateTime(core.ServiceDateFrom),
                string.Equals(core.MemberId, claim.MemberId, StringComparison.Ordinal), tier,
                Math.Max(0m, result.DeductibleAmount),
                Math.Max(0m, result.OopAppliedAmount ?? result.PatientResponsibility));
        }
        return new PriorAccumulation { Entries = entries };
    }

    private static void Add(
        List<PriorAccumulationEntry> entries, DateOnly date, bool thisMember, NetworkTier tier, decimal deductible, decimal outOfPocket)
    {
        if (deductible == 0m && outOfPocket == 0m) return;
        entries.Add(new PriorAccumulationEntry
        {
            ServiceDate = date, ThisMember = thisMember, NetworkTier = tier, Deductible = deductible, OutOfPocket = outOfPocket,
        });
    }

    private static NetworkTier TierOf(string? tier)
        => string.Equals(tier, nameof(NetworkTier.OutOfNetwork), StringComparison.OrdinalIgnoreCase)
            ? NetworkTier.OutOfNetwork
            : NetworkTier.InNetwork;

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
