using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// Mutable working copy of accumulator state for a single claim adjudication.
///
/// This is created at the start of benefit calculation, mutated as each
/// line is processed, and then persisted at the end. The pending updates
/// are applied via optimistic concurrency in the accumulator repository.
///
/// Why a working set instead of direct DB writes per line:
///   - A claim with 10 lines would otherwise require 10+ DB round trips
///     just for deductible/OOP tracking
///   - Cross-line interactions (e.g., OOP max reached partway through)
///     need consistent state within the claim
///   - If adjudication fails, nothing is persisted (atomic)
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

        // Initialize from current persisted state
        foreach (var acc in currentState)
        {
            var key = MakeKey(acc.Type, acc.Scope, acc.NetworkTier);
            _entries[key] = new AccumulatorEntry
            {
                Type = acc.Type,
                Scope = acc.Scope,
                NetworkTier = acc.NetworkTier,
                LimitAmount = acc.LimitAmount,
                OriginalAccumulated = acc.AccumulatedAmountAfter, // Current state is "after" last claim
                CurrentAccumulated = acc.AccumulatedAmountAfter,
            };
        }

        // Ensure standard accumulators exist even if no prior state
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

    // ═══════════════════════════════════════════════════════════════════
    // DEDUCTIBLE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// How much deductible remains for this member in this network tier.
    /// Considers both individual and family (embedded model).
    /// </summary>
    public decimal GetRemainingDeductible(NetworkTier networkTier)
    {
        var individualKey = MakeKey(AccumulatorType.IndividualDeductible,
            AccumulatorScope.Individual, networkTier);
        var familyKey = MakeKey(AccumulatorType.FamilyDeductible,
            AccumulatorScope.Family, networkTier);

        var individual = _entries.GetValueOrDefault(individualKey);
        var family = _entries.GetValueOrDefault(familyKey);

        if (individual is null) return 0;

        var individualRemaining = Math.Max(0, individual.LimitAmount - individual.CurrentAccumulated);

        // Embedded model: if family deductible is met, individual is also met
        if (_plan.FamilyAccumulatorModel == FamilyAccumulatorModel.Embedded && family is not null)
        {
            var familyRemaining = Math.Max(0, family.LimitAmount - family.CurrentAccumulated);
            if (familyRemaining <= 0)
            {
                // Family aggregate met — everyone's deductible is satisfied
                return 0;
            }
        }

        return individualRemaining;
    }

    /// <summary>
    /// Apply deductible spend. Updates both individual and family accumulators.
    /// </summary>
    public void ApplyDeductible(decimal amount, NetworkTier networkTier)
    {
        if (amount <= 0) return;

        var individualKey = MakeKey(AccumulatorType.IndividualDeductible,
            AccumulatorScope.Individual, networkTier);
        var familyKey = MakeKey(AccumulatorType.FamilyDeductible,
            AccumulatorScope.Family, networkTier);

        if (_entries.TryGetValue(individualKey, out var individual))
        {
            individual.CurrentAccumulated += amount;
            RecordUpdate(individual, amount, "Deductible");
        }

        if (_entries.TryGetValue(familyKey, out var family))
        {
            family.CurrentAccumulated += amount;
            RecordUpdate(family, amount, "Deductible-Family");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // OUT-OF-POCKET MAX
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// How much OOP remains before the member hits the maximum.
    /// </summary>
    public decimal GetRemainingOopMax(NetworkTier networkTier)
    {
        var individualKey = MakeKey(AccumulatorType.IndividualOutOfPocketMax,
            AccumulatorScope.Individual, networkTier);
        var familyKey = MakeKey(AccumulatorType.FamilyOutOfPocketMax,
            AccumulatorScope.Family, networkTier);

        var individual = _entries.GetValueOrDefault(individualKey);
        var family = _entries.GetValueOrDefault(familyKey);

        if (individual is null) return decimal.MaxValue; // No OOP max configured

        var individualRemaining = Math.Max(0, individual.LimitAmount - individual.CurrentAccumulated);

        // Embedded model: family OOP max can also cap
        if (_plan.FamilyAccumulatorModel == FamilyAccumulatorModel.Embedded && family is not null)
        {
            var familyRemaining = Math.Max(0, family.LimitAmount - family.CurrentAccumulated);
            if (familyRemaining <= 0)
            {
                return 0; // Family OOP max met — everything covered
            }
        }

        return individualRemaining;
    }

    /// <summary>
    /// Apply member responsibility to OOP max tracking.
    /// </summary>
    public void ApplyOopMax(decimal memberResponsibility, NetworkTier networkTier)
    {
        if (memberResponsibility <= 0) return;

        var individualKey = MakeKey(AccumulatorType.IndividualOutOfPocketMax,
            AccumulatorScope.Individual, networkTier);
        var familyKey = MakeKey(AccumulatorType.FamilyOutOfPocketMax,
            AccumulatorScope.Family, networkTier);

        if (_entries.TryGetValue(individualKey, out var individual))
        {
            individual.CurrentAccumulated += memberResponsibility;
            RecordUpdate(individual, memberResponsibility, "OOP");
        }

        if (_entries.TryGetValue(familyKey, out var family))
        {
            family.CurrentAccumulated += memberResponsibility;
            RecordUpdate(family, memberResponsibility, "OOP-Family");
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
                NetworkTier = NetworkTier.InNetwork, // Visits usually not network-specific
                LimitAmount = 0, // Set from benefit category
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
    // SNAPSHOT / PERSISTENCE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get the current state of all accumulators for the response.
    /// </summary>
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

    /// <summary>
    /// Get pending updates to persist to the accumulator store.
    /// These are applied with optimistic concurrency in the repository.
    /// </summary>
    public IReadOnlyList<AccumulatorUpdate> GetPendingUpdates() => _pendingUpdates;

    // ═══════════════════════════════════════════════════════════════════
    // INTERNALS
    // ═══════════════════════════════════════════════════════════════════

    private void EnsureAccumulator(
        AccumulatorType type, AccumulatorScope scope,
        NetworkTier networkTier, decimal limitAmount)
    {
        var key = MakeKey(type, scope, networkTier);
        if (!_entries.ContainsKey(key))
        {
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

/// <summary>
/// A single pending accumulator update to persist.
/// </summary>
public record AccumulatorUpdate
{
    public AccumulatorType Type { get; init; }
    public AccumulatorScope Scope { get; init; }
    public NetworkTier NetworkTier { get; init; }
    public decimal Amount { get; init; }
    public string Source { get; init; } = default!;
}
