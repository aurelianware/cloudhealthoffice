using CloudHealthOffice.BenefitEngine.Domain;

namespace CloudHealthOffice.BenefitEngine.Persistence;

// ═══════════════════════════════════════════════════════════════════
// ACCUMULATOR DOCUMENT
//
// Two documents are maintained per member per benefit plan year:
//
//   Individual document — keyed by memberId
//     Tracks: IndividualDeductible, IndividualOopMax, VisitCount,
//             DollarLimit, DayCount (all scoped to this member)
//
//   Family document — keyed by subscriberId
//     Tracks: FamilyDeductible, FamilyOopMax (aggregated across
//             all members on the subscriber's enrollment)
//
// Document ID pattern:
//   "{tenantId}:{scope}:{ownerId}:{benefitPlanId}:{planYear}"
//
// Optimistic concurrency:
//   MongoDB: Version field in the replace filter ensures no silent overwrites
//   Cosmos:  CosmosETag populated from response, used as IfMatchEtag
//
// Idempotency:
//   Each claim's contribution is recorded in the Transactions list.
//   Before applying updates, the service checks whether claimId already
//   appears in an active (non-reversed) transaction. If so, it skips
//   the write. This prevents double-counting when Argo retries a step.
//
// Reversal:
//   When a claim is voided or adjusted, ReverseAsync negates the amounts
//   from each affected balance entry and marks the transaction IsReversed.
//   The original amounts are preserved in the transaction log for audit.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Persisted accumulator state for a single owner (member or subscriber)
/// within a benefit plan year.
/// </summary>
public class AccumulatorDocument
{
    /// <summary>
    /// Composite document ID.
    /// Format: "{tenantId}:{scope}:{ownerId}:{benefitPlanId}:{planYear}"
    /// </summary>
    public string Id { get; set; } = default!;

    public string TenantId { get; set; } = default!;

    /// <summary>
    /// memberId for Individual scope; subscriberId for Family scope.
    /// </summary>
    public string OwnerId { get; set; } = default!;

    /// <summary>
    /// "Individual" or "Family" (AccumulatorScope enum name).
    /// </summary>
    public string Scope { get; set; } = default!;

    public Guid BenefitPlanId { get; set; }
    public string PlanYear { get; set; } = default!;

    /// <summary>
    /// Optimistic concurrency stamp. Incremented on every successful write.
    /// MongoDB uses this in the replace filter (Eq Version == expectedVersion).
    /// Cosmos uses ETag from the response (stored separately in CosmosETag).
    /// </summary>
    public long Version { get; set; }

    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Current accumulated balance for each accumulator type / network tier.
    /// </summary>
    public List<AccumulatorBalance> Balances { get; set; } = [];

    /// <summary>
    /// Audit trail of claim contributions. Used for idempotency and reversal.
    /// </summary>
    public List<AccumulatorTransaction> Transactions { get; set; } = [];

    /// <summary>
    /// ETag from the last Cosmos read/write. Not persisted in the document body —
    /// populated from ItemResponse.ETag after every Cosmos operation.
    /// Used as IfMatchEtag on subsequent writes for optimistic concurrency.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CosmosETag { get; set; }

    public static string MakeId(
        string tenantId, string scope, string ownerId,
        Guid benefitPlanId, string planYear)
        => $"{tenantId}:{scope}:{ownerId}:{benefitPlanId}:{planYear}";
}

/// <summary>
/// Accumulated amount for a single accumulator type and network tier.
/// </summary>
public class AccumulatorBalance
{
    /// <summary>AccumulatorType enum name (e.g., "IndividualDeductible").</summary>
    public string Type { get; set; } = default!;

    /// <summary>NetworkTier enum name (e.g., "InNetwork").</summary>
    public string NetworkTier { get; set; } = default!;

    /// <summary>
    /// Plan-level cap for this accumulator (carried for convenience;
    /// authoritative value is always in BenefitPlanConfig).
    /// </summary>
    public decimal LimitAmount { get; set; }

    /// <summary>Amount accumulated so far this plan year.</summary>
    public decimal AccumulatedAmount { get; set; }
}

/// <summary>
/// One claim's contribution to accumulators.
/// Enables idempotent re-processing and full reversal of voided claims.
/// </summary>
public class AccumulatorTransaction
{
    public string ClaimId { get; set; } = default!;
    public DateTime AppliedAt { get; set; }
    public bool IsReversed { get; set; }
    public DateTime? ReversedAt { get; set; }

    /// <summary>
    /// Per-bucket amounts applied by this claim.
    /// </summary>
    public List<AccumulatorTransactionEntry> Entries { get; set; } = [];
}

/// <summary>
/// Amount applied to a single accumulator bucket within one transaction.
/// </summary>
public class AccumulatorTransactionEntry
{
    /// <summary>AccumulatorType enum name.</summary>
    public string Type { get; set; } = default!;

    /// <summary>NetworkTier enum name.</summary>
    public string NetworkTier { get; set; } = default!;

    public decimal AmountApplied { get; set; }

    /// <summary>
    /// Human-readable source label (e.g., "Deductible", "OOP", "VisitCount:98").
    /// For audit and debugging.
    /// </summary>
    public string Source { get; set; } = default!;
}
