using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;

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

    public AccumulatorWorkingSet(
        IReadOnlyList<AccumulatorSnapshot> currentState,
        BenefitPlanConfig plan)
    {
        _plan = plan;

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

        // Ensure standard accumulators exist based on family model
        if (plan.FamilyAccumulatorModel == FamilyAccumulatorModel.Aggregate)
        {
            // Aggregate: family-level accumulators are the primary pool.
            EnsureAccumulator(AccumulatorType.FamilyDeductible, AccumulatorScope.Family,
                NetworkTier.InNetwork, plan.FamilyDeductible ?? 0);
            EnsureAccumulator(AccumulatorType.FamilyDeductible, AccumulatorScope.Family,
                NetworkTier.OutOfNetwork, plan.FamilyDeductibleOon ?? plan.FamilyDeductible ?? 0);
            EnsureAccumulator(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family,
                NetworkTier.InNetwork, plan.FamilyOopMax ?? 0);
            EnsureAccumulator(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family,
                NetworkTier.OutOfNetwork, plan.FamilyOopMaxOon ?? plan.FamilyOopMax ?? 0);

            // ACA 45 CFR §156.130 individual cap. Gated by IsAcaCapEnforced
            // so legacy Aggregate plans don't surprise-cap mid-year. The
            // accumulator is scoped Individual so cap state is per-member;
            // each member's working-set hydrates only their own row.
            if (plan.IsAcaCapEnforced && plan.AcaIndividualCap is decimal cap && cap > 0)
            {
                EnsureAccumulator(AccumulatorType.AcaIndividualCap, AccumulatorScope.Individual,
                    NetworkTier.InNetwork, cap);
                EnsureAccumulator(AccumulatorType.AcaIndividualCap, AccumulatorScope.Individual,
                    NetworkTier.OutOfNetwork, cap);
            }
        }
        else
        {
            // Embedded: individual + family
            EnsureAccumulator(AccumulatorType.IndividualDeductible, AccumulatorScope.Individual,
                NetworkTier.InNetwork, plan.IndividualDeductible ?? 0);
            EnsureAccumulator(AccumulatorType.IndividualDeductible, AccumulatorScope.Individual,
                NetworkTier.OutOfNetwork, plan.IndividualDeductibleOon ?? plan.IndividualDeductible ?? 0);
            EnsureAccumulator(AccumulatorType.FamilyDeductible, AccumulatorScope.Family,
                NetworkTier.InNetwork, plan.FamilyDeductible ?? 0);
            EnsureAccumulator(AccumulatorType.FamilyDeductible, AccumulatorScope.Family,
                NetworkTier.OutOfNetwork, plan.FamilyDeductibleOon ?? plan.FamilyDeductible ?? 0);
            EnsureAccumulator(AccumulatorType.IndividualOutOfPocketMax, AccumulatorScope.Individual,
                NetworkTier.InNetwork, plan.IndividualOopMax ?? 0);
            EnsureAccumulator(AccumulatorType.IndividualOutOfPocketMax, AccumulatorScope.Individual,
                NetworkTier.OutOfNetwork, plan.IndividualOopMaxOon ?? plan.IndividualOopMax ?? 0);
            EnsureAccumulator(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family,
                NetworkTier.InNetwork, plan.FamilyOopMax ?? 0);
            EnsureAccumulator(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family,
                NetworkTier.OutOfNetwork, plan.FamilyOopMaxOon ?? plan.FamilyOopMax ?? 0);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // DEDUCTIBLE
    // ═══════════════════════════════════════════════════════════════════

    public decimal GetRemainingDeductible(NetworkTier networkTier)
    {
        if (_plan.FamilyAccumulatorModel == FamilyAccumulatorModel.Aggregate)
        {
            // Aggregate: only family pool exists — no individual sub-limit
            var familyKey = MakeKey(AccumulatorType.FamilyDeductible,
                AccumulatorScope.Family, networkTier);
            var family = _entries.GetValueOrDefault(familyKey);
            return family is null ? 0 : Math.Max(0, family.LimitAmount - family.CurrentAccumulated);
        }

        // Embedded model
        var individualKey = MakeKey(AccumulatorType.IndividualDeductible,
            AccumulatorScope.Individual, networkTier);
        var familyKeyEmb = MakeKey(AccumulatorType.FamilyDeductible,
            AccumulatorScope.Family, networkTier);

        var individual = _entries.GetValueOrDefault(individualKey);
        var familyEmb = _entries.GetValueOrDefault(familyKeyEmb);

        if (individual is null) return 0;

        var individualRemaining = Math.Max(0, individual.LimitAmount - individual.CurrentAccumulated);

        // Embedded: if family deductible is met, individual is also met
        if (familyEmb is not null)
        {
            var familyRemaining = Math.Max(0, familyEmb.LimitAmount - familyEmb.CurrentAccumulated);
            if (familyRemaining <= 0)
                return 0;
        }

        return individualRemaining;
    }

    public void ApplyDeductible(decimal amount, NetworkTier networkTier)
    {
        if (amount <= 0) return;

        if (_plan.FamilyAccumulatorModel == FamilyAccumulatorModel.Aggregate)
        {
            // Aggregate: only update family pool
            var familyKey = MakeKey(AccumulatorType.FamilyDeductible,
                AccumulatorScope.Family, networkTier);
            if (_entries.TryGetValue(familyKey, out var family))
            {
                family.CurrentAccumulated += amount;
                RecordUpdate(family, amount, "Deductible-Family-Aggregate");
            }
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

    // ═══════════════════════════════════════════════════════════════════
    // OUT-OF-POCKET MAX
    // ═══════════════════════════════════════════════════════════════════

    public decimal GetRemainingOopMax(NetworkTier networkTier)
    {
        if (_plan.FamilyAccumulatorModel == FamilyAccumulatorModel.Aggregate)
        {
            // Aggregate: family pool is primary; ACA per-member cap (when
            // enforced) clamps how much of the pool one member may absorb.
            var familyKey = MakeKey(AccumulatorType.FamilyOutOfPocketMax,
                AccumulatorScope.Family, networkTier);
            var family = _entries.GetValueOrDefault(familyKey);
            if (family is null) return decimal.MaxValue;
            var familyRemaining = Math.Max(0, family.LimitAmount - family.CurrentAccumulated);

            var capRemaining = GetAcaIndividualCapRemaining(networkTier);
            return capRemaining is decimal c ? Math.Min(familyRemaining, c) : familyRemaining;
        }

        // Embedded model
        var individualKey = MakeKey(AccumulatorType.IndividualOutOfPocketMax,
            AccumulatorScope.Individual, networkTier);
        var familyKeyEmb = MakeKey(AccumulatorType.FamilyOutOfPocketMax,
            AccumulatorScope.Family, networkTier);

        var individual = _entries.GetValueOrDefault(individualKey);
        var familyEmb = _entries.GetValueOrDefault(familyKeyEmb);

        if (individual is null) return decimal.MaxValue;

        var individualRemaining = Math.Max(0, individual.LimitAmount - individual.CurrentAccumulated);

        if (familyEmb is not null)
        {
            var familyRemaining = Math.Max(0, familyEmb.LimitAmount - familyEmb.CurrentAccumulated);
            if (familyRemaining <= 0)
                return 0;
        }

        return individualRemaining;
    }

    public void ApplyOopMax(decimal memberResponsibility, NetworkTier networkTier)
    {
        if (memberResponsibility <= 0) return;

        if (_plan.FamilyAccumulatorModel == FamilyAccumulatorModel.Aggregate)
        {
            var familyKey = MakeKey(AccumulatorType.FamilyOutOfPocketMax,
                AccumulatorScope.Family, networkTier);
            if (_entries.TryGetValue(familyKey, out var family))
            {
                family.CurrentAccumulated += memberResponsibility;
                RecordUpdate(family, memberResponsibility, "OOP-Family-Aggregate");
            }

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
        if (!_entries.TryGetValue(key, out var entry)) return null;

        return Math.Max(0, entry.LimitAmount - entry.CurrentAccumulated);
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
