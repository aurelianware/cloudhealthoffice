using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// Mutable working copy of accumulator state for a single claim adjudication.
///
/// Supports both Embedded and Aggregate family accumulator models:
///   Embedded:  individual + family accumulators tracked independently.
///              Individual met → that member done. Family met → all members done.
///   Aggregate: family-only pool. Per ACA 45 CFR §156.130 each member also
///              has an ACA individual cap that bounds how much of the family
///              pool a single member can absorb. The cap is only seeded /
///              enforced when <see cref="BenefitPlanConfig.IsAcaCapEnforced"/>
///              is true and <see cref="BenefitPlanConfig.AcaIndividualCap"/>
///              is set; legacy plans pre-5.7 hydrate with both false / null
///              and behave exactly as before (single shared family pool).
///              When a family limit is unset, the member's individual limit
///              is the pool for that accumulator.
///
/// QNXT equivalent: The in-memory accumulator state that QNXT's
/// adjudication engine maintains during claim processing, then writes
/// back to the AccumBalance tables.
/// </summary>
public class AccumulatorWorkingSet
{
    private readonly Dictionary<string, AccumulatorEntry> _entries = new();
    private readonly List<AccumulatorUpdate> _pendingUpdates = [];
    private readonly BenefitPlanConfig _plan;
    private readonly ILogger _logger;

    // Keys whose limit the plan leaves unset. A zero-limit entry under one
    // of these keys (e.g. a source-system placeholder) means "no limit",
    // never "limit already met".
    private readonly HashSet<string> _unsetLimits = new();

    public AccumulatorWorkingSet(
        IReadOnlyList<AccumulatorSnapshot> currentState,
        BenefitPlanConfig plan,
        ILogger? logger = null)
    {
        _plan = plan;
        _logger = logger ?? NullLogger.Instance;

        foreach (var acc in currentState)
        {
            var key = acc.Type switch
            {
                AccumulatorType.VisitCount  when acc.ServiceTypeCode is not null
                    => $"VisitCount:{acc.ServiceTypeCode}",
                AccumulatorType.DayCount    when acc.ServiceTypeCode is not null
                    => $"DayCount:{acc.ServiceTypeCode}",
                AccumulatorType.DollarLimit when acc.ServiceTypeCode is not null
                    => $"DollarLimit:{acc.ServiceTypeCode}",
                _ => MakeKey(acc.Type, acc.Scope, acc.NetworkTier)
            };

            _entries[key] = new AccumulatorEntry
            {
                Type = acc.Type,
                Scope = acc.Scope,
                NetworkTier = acc.NetworkTier,
                LimitAmount = acc.LimitAmount,
                OriginalAccumulated = acc.AccumulatedAmountAfter,
                CurrentAccumulated = acc.AccumulatedAmountAfter,
            };
        }

        // Ensure standard accumulators exist based on family model.
        // Null limit = not configured: no accumulator is seeded and the
        // limit does not constrain the member (see SeedDeductible /
        // SeedOopMax for the 0-vs-null rules).
        if (plan.FamilyAccumulatorModel == FamilyAccumulatorModel.Aggregate)
        {
            // Aggregate: family-level accumulators are the primary pool.
            SeedDeductible(AccumulatorType.FamilyDeductible, AccumulatorScope.Family,
                NetworkTier.InNetwork, plan.FamilyDeductible);
            SeedDeductible(AccumulatorType.FamilyDeductible, AccumulatorScope.Family,
                NetworkTier.OutOfNetwork, plan.FamilyDeductibleOon ?? plan.FamilyDeductible);
            SeedOopMax(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family,
                NetworkTier.InNetwork, plan.FamilyOopMax);
            SeedOopMax(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family,
                NetworkTier.OutOfNetwork, plan.FamilyOopMaxOon ?? plan.FamilyOopMax);

            // No family limit configured (e.g. employee-only coverage): the
            // member's individual limit is the pool, so a configured
            // individual deductible / OOP max is not silently dropped.
            // Seeded per tier only where the family limit is unset.
            SeedAggregateIndividualFallback(NetworkTier.InNetwork,
                plan.IndividualDeductible, plan.IndividualOopMax);
            SeedAggregateIndividualFallback(NetworkTier.OutOfNetwork,
                plan.IndividualDeductibleOon ?? plan.IndividualDeductible,
                plan.IndividualOopMaxOon ?? plan.IndividualOopMax);

            // ACA 45 CFR §156.130 individual cap. Gated by IsAcaCapEnforced
            // so legacy Aggregate plans don't surprise-cap mid-year. The
            // accumulator is scoped Individual so cap state is per-member;
            // each member's working-set hydrates only their own row.
            var cap = plan.IsAcaCapEnforced && plan.AcaIndividualCap is decimal c && c > 0
                ? c
                : (decimal?)null;
            SeedOopMax(AccumulatorType.AcaIndividualCap, AccumulatorScope.Individual,
                NetworkTier.InNetwork, cap);
            SeedOopMax(AccumulatorType.AcaIndividualCap, AccumulatorScope.Individual,
                NetworkTier.OutOfNetwork, cap);
        }
        else
        {
            // Embedded: individual + family
            SeedDeductible(AccumulatorType.IndividualDeductible, AccumulatorScope.Individual,
                NetworkTier.InNetwork, plan.IndividualDeductible);
            SeedDeductible(AccumulatorType.IndividualDeductible, AccumulatorScope.Individual,
                NetworkTier.OutOfNetwork, plan.IndividualDeductibleOon ?? plan.IndividualDeductible);
            SeedDeductible(AccumulatorType.FamilyDeductible, AccumulatorScope.Family,
                NetworkTier.InNetwork, plan.FamilyDeductible);
            SeedDeductible(AccumulatorType.FamilyDeductible, AccumulatorScope.Family,
                NetworkTier.OutOfNetwork, plan.FamilyDeductibleOon ?? plan.FamilyDeductible);
            SeedOopMax(AccumulatorType.IndividualOutOfPocketMax, AccumulatorScope.Individual,
                NetworkTier.InNetwork, plan.IndividualOopMax);
            SeedOopMax(AccumulatorType.IndividualOutOfPocketMax, AccumulatorScope.Individual,
                NetworkTier.OutOfNetwork, plan.IndividualOopMaxOon ?? plan.IndividualOopMax);
            SeedOopMax(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family,
                NetworkTier.InNetwork, plan.FamilyOopMax);
            SeedOopMax(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family,
                NetworkTier.OutOfNetwork, plan.FamilyOopMaxOon ?? plan.FamilyOopMax);
        }
    }

    /// <summary>
    /// Deductible limits: null = not configured (no accumulator; does not
    /// constrain the member). An explicit 0 is a real "$0 deductible" —
    /// nothing is owed toward it.
    /// </summary>
    private void SeedDeductible(
        AccumulatorType type, AccumulatorScope scope, NetworkTier tier, decimal? limit)
    {
        if (limit is null)
        {
            _unsetLimits.Add(MakeKey(type, scope, tier));
            return;
        }

        EnsureAccumulator(type, scope, tier, Math.Max(0, limit.Value));
    }

    /// <summary>
    /// OOP-max limits: null = not configured (uncapped). A 0 or negative
    /// OOP max would shift every dollar of cost share to the plan, which is
    /// never an intended design, so it is treated as unset and logged.
    /// </summary>
    private void SeedOopMax(
        AccumulatorType type, AccumulatorScope scope, NetworkTier tier, decimal? limit)
    {
        if (limit is <= 0)
        {
            _logger.LogWarning(
                "Plan {PlanId} configures {AccumulatorType} {NetworkTier} as {Limit}; treating as unset (no cap)",
                _plan.Id, type, tier, limit);
            limit = null;
        }

        if (limit is null)
        {
            _unsetLimits.Add(MakeKey(type, scope, tier));
            return;
        }

        EnsureAccumulator(type, scope, tier, limit.Value);
    }

    private void SeedAggregateIndividualFallback(
        NetworkTier tier, decimal? individualDeductible, decimal? individualOopMax)
    {
        if (IsFamilyUnset(AccumulatorType.FamilyDeductible, tier))
            SeedDeductible(AccumulatorType.IndividualDeductible, AccumulatorScope.Individual,
                tier, individualDeductible);
        if (IsFamilyUnset(AccumulatorType.FamilyOutOfPocketMax, tier))
            SeedOopMax(AccumulatorType.IndividualOutOfPocketMax, AccumulatorScope.Individual,
                tier, individualOopMax);
    }

    private bool IsFamilyUnset(AccumulatorType familyType, NetworkTier tier)
        => _unsetLimits.Contains(MakeKey(familyType, AccumulatorScope.Family, tier));

    /// <summary>
    /// Aggregate-mode pool for <paramref name="familyType"/>: the family
    /// accumulator, or — only when the plan leaves the family limit unset —
    /// the member's individual accumulator.
    /// </summary>
    private AccumulatorEntry? GetAggregatePool(
        AccumulatorType familyType, AccumulatorType individualType, NetworkTier tier)
    {
        var family = GetLimitingEntry(MakeKey(familyType, AccumulatorScope.Family, tier));
        if (family is not null || !IsFamilyUnset(familyType, tier)) return family;
        return GetLimitingEntry(MakeKey(individualType, AccumulatorScope.Individual, tier));
    }

    /// <summary>
    /// Records <paramref name="amount"/> in exactly one Aggregate pool: the
    /// family accumulator, or — when the plan leaves the family limit unset —
    /// the member's individual fallback. A zero-limit family placeholder is
    /// not also incremented, so the same dollars never land in two pools.
    /// </summary>
    private void ApplyToAggregatePool(
        AccumulatorType familyType, AccumulatorType individualType,
        NetworkTier tier, decimal amount, string familySource, string individualSource)
    {
        var (key, source) = IsFamilyUnset(familyType, tier)
            ? (MakeKey(individualType, AccumulatorScope.Individual, tier), individualSource)
            : (MakeKey(familyType, AccumulatorScope.Family, tier), familySource);

        if (_entries.TryGetValue(key, out var pool))
        {
            pool.CurrentAccumulated += amount;
            RecordUpdate(pool, amount, source);
        }
    }

    /// <summary>
    /// Returns the entry for <paramref name="key"/> when it constrains the
    /// member: present, and not a zero-limit placeholder for a limit the
    /// plan leaves unset.
    /// </summary>
    private AccumulatorEntry? GetLimitingEntry(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        if (entry.LimitAmount <= 0 && _unsetLimits.Contains(key)) return null;
        return entry;
    }

    private static decimal Remaining(AccumulatorEntry entry)
        => Math.Max(0, entry.LimitAmount - entry.CurrentAccumulated);

    // ═══════════════════════════════════════════════════════════════════
    // DEDUCTIBLE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The deductible still to apply when pricing this claim's next unit.
    /// Includes any NAIC COB credit given on this claim's earlier units
    /// (see <see cref="ApplyDeductibleWithCredit"/>): deductible the plan
    /// credited is met for the rest of the claim too.
    /// </summary>
    public decimal GetRemainingDeductible(NetworkTier networkTier)
        => RemainingDeductible(networkTier, Remaining);

    private decimal RemainingDeductible(NetworkTier networkTier, Func<AccumulatorEntry, decimal> remaining)
    {
        if (_plan.FamilyAccumulatorModel == FamilyAccumulatorModel.Aggregate)
        {
            // Aggregate: single pool — no individual sub-limit, except as
            // the fallback pool when no family deductible is configured.
            var pool = GetAggregatePool(AccumulatorType.FamilyDeductible,
                AccumulatorType.IndividualDeductible, networkTier);
            return pool is null ? 0 : remaining(pool);
        }

        // Embedded model: the member owes toward the deductible until either
        // their individual or the family limit is met. No configured limit
        // at all means no deductible.
        var individual = GetLimitingEntry(MakeKey(AccumulatorType.IndividualDeductible,
            AccumulatorScope.Individual, networkTier));
        var familyEmb = GetLimitingEntry(MakeKey(AccumulatorType.FamilyDeductible,
            AccumulatorScope.Family, networkTier));

        if (individual is null && familyEmb is null) return 0;
        if (individual is null) return remaining(familyEmb!);
        if (familyEmb is null) return remaining(individual);
        return Math.Min(remaining(individual), remaining(familyEmb));
    }

    public void ApplyDeductible(decimal amount, NetworkTier networkTier)
    {
        if (amount <= 0) return;

        if (_plan.FamilyAccumulatorModel == FamilyAccumulatorModel.Aggregate)
        {
            // Aggregate: only update the single pool
            ApplyToAggregatePool(AccumulatorType.FamilyDeductible, AccumulatorType.IndividualDeductible,
                networkTier, amount, "Deductible-Family-Aggregate", "Deductible");
            return;
        }

        // Embedded: update both individual and family
        var individualKey = MakeKey(AccumulatorType.IndividualDeductible,
            AccumulatorScope.Individual, networkTier);
        var familyKeyEmb = MakeKey(AccumulatorType.FamilyDeductible,
            AccumulatorScope.Family, networkTier);

        if (_entries.TryGetValue(individualKey, out var ind))
        {
            ind.CurrentAccumulated += amount;
            RecordUpdate(ind, amount, "Deductible");
        }

        if (_entries.TryGetValue(familyKeyEmb, out var fam))
        {
            fam.CurrentAccumulated += amount;
            RecordUpdate(fam, amount, "Deductible-Family");
        }
    }

    /// <summary>
    /// Credits the deductible for one COB unit (a line, or a DRG stay) and
    /// returns the amount credited. <paramref name="memberOwes"/> is the
    /// deductible on the 835 (PR-1); <paramref name="toCredit"/> is what the
    /// plan's setting credits — the same amount, or under NAIC full credit
    /// the deductible the plan applied before COB (NAIC MDL-120 §7).
    /// The credit is limited to the deductible left in the accumulators (it
    /// never takes them past the limit). It counts as met for this claim's
    /// later units as well as for the claims that follow: the plan prices the
    /// rest of the claim as it would have with no other coverage.
    /// </summary>
    public decimal ApplyDeductibleWithCredit(decimal memberOwes, decimal toCredit, NetworkTier networkTier)
    {
        memberOwes = Math.Max(0, memberOwes);
        toCredit = Math.Max(memberOwes, toCredit);
        var credited = toCredit == memberOwes
            ? memberOwes
            : Math.Min(toCredit, Math.Max(memberOwes, GetRemainingDeductible(networkTier)));
        ApplyDeductible(credited, networkTier);
        return credited;
    }

    // ═══════════════════════════════════════════════════════════════════
    // OUT-OF-POCKET MAX
    // ═══════════════════════════════════════════════════════════════════

    public decimal GetRemainingOopMax(NetworkTier networkTier)
    {
        if (_plan.FamilyAccumulatorModel == FamilyAccumulatorModel.Aggregate)
        {
            // Aggregate: family pool is primary; ACA per-member cap (when
            // enforced) clamps how much of the pool one member may absorb.
            var pool = GetAggregatePool(AccumulatorType.FamilyOutOfPocketMax,
                AccumulatorType.IndividualOutOfPocketMax, networkTier);
            var familyRemaining = pool is null ? decimal.MaxValue : Remaining(pool);

            var capRemaining = GetAcaIndividualCapRemaining(networkTier);
            return capRemaining is decimal c ? Math.Min(familyRemaining, c) : familyRemaining;
        }

        // Embedded model: capped by whichever configured limit is closer.
        // No configured limit means uncapped.
        var individual = GetLimitingEntry(MakeKey(AccumulatorType.IndividualOutOfPocketMax,
            AccumulatorScope.Individual, networkTier));
        var familyEmb = GetLimitingEntry(MakeKey(AccumulatorType.FamilyOutOfPocketMax,
            AccumulatorScope.Family, networkTier));

        var remaining = decimal.MaxValue;
        if (individual is not null) remaining = Math.Min(remaining, Remaining(individual));
        if (familyEmb is not null) remaining = Math.Min(remaining, Remaining(familyEmb));
        return remaining;
    }

    public void ApplyOopMax(decimal memberResponsibility, NetworkTier networkTier)
    {
        if (memberResponsibility <= 0) return;

        if (_plan.FamilyAccumulatorModel == FamilyAccumulatorModel.Aggregate)
        {
            ApplyToAggregatePool(AccumulatorType.FamilyOutOfPocketMax, AccumulatorType.IndividualOutOfPocketMax,
                networkTier, memberResponsibility, "OOP-Family-Aggregate", "OOP");

            // ACA per-member cap accumulates in lockstep with family pool
            // when enforced. Mirrors the Embedded dual-update pattern below.
            var capKey = MakeKey(AccumulatorType.AcaIndividualCap,
                AccumulatorScope.Individual, networkTier);
            if (_entries.TryGetValue(capKey, out var cap))
            {
                cap.CurrentAccumulated += memberResponsibility;
                RecordUpdate(cap, memberResponsibility, "OOP-AcaIndividualCap");
            }
            return;
        }

        // Embedded
        var individualKey = MakeKey(AccumulatorType.IndividualOutOfPocketMax,
            AccumulatorScope.Individual, networkTier);
        var familyKeyEmb = MakeKey(AccumulatorType.FamilyOutOfPocketMax,
            AccumulatorScope.Family, networkTier);

        if (_entries.TryGetValue(individualKey, out var ind))
        {
            ind.CurrentAccumulated += memberResponsibility;
            RecordUpdate(ind, memberResponsibility, "OOP");
        }

        if (_entries.TryGetValue(familyKeyEmb, out var fam))
        {
            fam.CurrentAccumulated += memberResponsibility;
            RecordUpdate(fam, memberResponsibility, "OOP-Family");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // VISIT / DAY / DOLLAR COUNTERS
    // ═══════════════════════════════════════════════════════════════════

    public int GetVisitCount(string serviceTypeCode)
    {
        var key = $"VisitCount:{serviceTypeCode}";
        return _entries.TryGetValue(key, out var entry)
            ? (int)entry.CurrentAccumulated : 0;
    }

    public void IncrementVisitCount(string serviceTypeCode, int count)
    {
        var key = $"VisitCount:{serviceTypeCode}";
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new AccumulatorEntry
            {
                Type = AccumulatorType.VisitCount,
                Scope = AccumulatorScope.Individual,
                NetworkTier = NetworkTier.InNetwork,
                LimitAmount = 0,
                CurrentAccumulated = 0
            };
            _entries[key] = entry;
        }

        entry.CurrentAccumulated += count;
        RecordUpdate(entry, count, $"VisitCount:{serviceTypeCode}");
    }

    public int GetDayCount(string serviceTypeCode)
    {
        var key = $"DayCount:{serviceTypeCode}";
        return _entries.TryGetValue(key, out var entry)
            ? (int)entry.CurrentAccumulated : 0;
    }

    public decimal GetDollarAmount(string serviceTypeCode)
    {
        var key = $"DollarLimit:{serviceTypeCode}";
        return _entries.TryGetValue(key, out var entry)
            ? entry.CurrentAccumulated : 0;
    }

    // ═══════════════════════════════════════════════════════════════════
    // REVERSAL — for void/replace claims
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reverse all pending updates (used when the engine needs to unwind
    /// within the same adjudication). For cross-claim reversals, use
    /// IAccumulatorService.ReverseAsync which reads the original updates
    /// from persistent storage.
    /// </summary>
    public void ReversePendingUpdates()
    {
        foreach (var update in _pendingUpdates)
        {
            var key = update.Type switch
            {
                AccumulatorType.VisitCount => $"VisitCount:{update.Source.Split(':').LastOrDefault()}",
                AccumulatorType.DayCount => $"DayCount:{update.Source.Split(':').LastOrDefault()}",
                AccumulatorType.DollarLimit => $"DollarLimit:{update.Source.Split(':').LastOrDefault()}",
                _ => MakeKey(update.Type, update.Scope, update.NetworkTier)
            };

            if (_entries.TryGetValue(key, out var entry))
            {
                entry.CurrentAccumulated -= update.Amount;
            }
        }

        _pendingUpdates.Clear();
    }

    // ═══════════════════════════════════════════════════════════════════
    // SNAPSHOT / PERSISTENCE
    // ═══════════════════════════════════════════════════════════════════

    public List<AccumulatorState> GetSnapshot()
    {
        return _entries.Values.Select(e => new AccumulatorState
        {
            Type = e.Type,
            Scope = e.Scope,
            NetworkTier = e.NetworkTier,
            LimitAmount = e.LimitAmount,
            AccumulatedAmountBefore = e.OriginalAccumulated,
            AmountApplied = e.CurrentAccumulated - e.OriginalAccumulated,
            AccumulatedAmountAfter = e.CurrentAccumulated,
            RemainingAmount = Math.Max(0, e.LimitAmount - e.CurrentAccumulated),
            LimitReached = e.LimitAmount > 0 && e.CurrentAccumulated >= e.LimitAmount
        }).ToList();
    }

    public IReadOnlyList<AccumulatorUpdate> GetPendingUpdates() => _pendingUpdates;

    // ═══════════════════════════════════════════════════════════════════
    // INTERNALS
    // ═══════════════════════════════════════════════════════════════════

    private void EnsureAccumulator(
        AccumulatorType type, AccumulatorScope scope,
        NetworkTier networkTier, decimal limitAmount)
    {
        var key = MakeKey(type, scope, networkTier);
        if (_entries.TryGetValue(key, out var existing))
        {
            // Some source systems emit a zero-limit placeholder before a
            // member has accumulator activity. The authored plan remains the
            // source of truth for the limit; treating the placeholder as a
            // real zero OOP maximum incorrectly shifts all cost share to the plan.
            if (existing.LimitAmount <= 0 && limitAmount > 0)
            {
                existing.LimitAmount = limitAmount;
            }

            return;
        }

        _entries[key] = new AccumulatorEntry
        {
            Type = type,
            Scope = scope,
            NetworkTier = networkTier,
            LimitAmount = limitAmount,
            OriginalAccumulated = 0,
            CurrentAccumulated = 0
        };
    }

    private void RecordUpdate(AccumulatorEntry entry, decimal amount, string source)
    {
        _pendingUpdates.Add(new AccumulatorUpdate
        {
            Type = entry.Type,
            Scope = entry.Scope,
            NetworkTier = entry.NetworkTier,
            Amount = amount,
            Source = source
        });
    }

    /// <summary>
    /// Per-member ACA 45 CFR §156.130 individual-cap remaining for the
    /// given network tier. Returns null when the plan is not Aggregate
    /// mode, when enforcement is disabled, or when no cap accumulator was
    /// seeded — callers treat null as "no individual sub-limit applies".
    /// </summary>
    private decimal? GetAcaIndividualCapRemaining(NetworkTier networkTier)
    {
        if (_plan.FamilyAccumulatorModel != FamilyAccumulatorModel.Aggregate) return null;
        if (!_plan.IsAcaCapEnforced) return null;

        var key = MakeKey(AccumulatorType.AcaIndividualCap,
            AccumulatorScope.Individual, networkTier);
        var entry = GetLimitingEntry(key);
        return entry is null ? null : Remaining(entry);
    }

    private static string MakeKey(AccumulatorType type, AccumulatorScope scope, NetworkTier tier)
        => $"{type}:{scope}:{tier}";

    private class AccumulatorEntry
    {
        public AccumulatorType Type { get; init; }
        public AccumulatorScope Scope { get; init; }
        public NetworkTier NetworkTier { get; init; }
        public decimal LimitAmount { get; set; }
        public decimal OriginalAccumulated { get; init; }
        public decimal CurrentAccumulated { get; set; }
    }
}

public record AccumulatorUpdate
{
    public AccumulatorType Type { get; init; }
    public AccumulatorScope Scope { get; init; }
    public NetworkTier NetworkTier { get; init; }
    public decimal Amount { get; init; }
    public string Source { get; init; } = default!;
}
