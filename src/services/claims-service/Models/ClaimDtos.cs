using System.ComponentModel.DataAnnotations;

namespace ClaimsService.Models;

/// <summary>
/// Claim status update request (277 transaction)
/// </summary>
public class ClaimStatusUpdate
{
    [Required]
    public ClaimStatus Status { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

/// <summary>
/// Remittance update request (835 transaction). Sent by payment-service
/// during PaymentRun execution to transition a claim Approved/PartiallyPaid → Paid.
/// Also accepted from manual remittance-posting tools.
///
/// 5.10: the controller delegates to <c>IClaimFinalizationService</c>
/// when this lands so the version-event chain and Kafka notification
/// fire alongside the legacy Status update. <see cref="PaymentRunId"/>
/// and <see cref="EraEnvelopeId"/> are optional audit-trail
/// crumbs — present when payment-service is the caller, null when a
/// manual posting tool drives the endpoint.
/// </summary>
public class RemittanceUpdate
{
    [Required]
    [StringLength(50)]
    public string ControlNumber { get; set; } = string.Empty;

    [StringLength(50)]
    public string? CheckNumber { get; set; }

    public DateTime PaymentDate { get; set; } = DateTime.UtcNow;

    public decimal PaymentAmount { get; set; }

    /// <summary>Identifier of the originating payment run, for audit-trail crumbs.</summary>
    [StringLength(100)]
    public string? PaymentRunId { get; set; }

    /// <summary>Identifier of the EraEnvelope this claim was emitted within, for audit-trail crumbs.</summary>
    [StringLength(100)]
    public string? EraEnvelopeId { get; set; }
}

/// <summary>
/// Inbound payer 835 applied onto a domain claim. Distinct from
/// <see cref="RemittanceUpdate"/> (CHO-as-payer PaymentRun finalize).
/// </summary>
public class InboundRemittancePostRequest
{
    [Required]
    [StringLength(100)]
    public string RemittanceId { get; set; } = string.Empty;

    public decimal PaymentAmount { get; set; }

    public decimal PatientResponsibility { get; set; }

    public DateTime PaymentDate { get; set; }
}

/// <summary>
/// Accumulator totals response — aggregated cost-share amounts per bucket.
/// Returned by GET /api/claims/accumulator-totals; consumed by the Redis
/// accumulator service on a cache miss to rebuild from claim history.
/// </summary>
public class AccumulatorTotalsResponse
{
    /// <summary>One entry per (AccumulatorType × NetworkTier) combination that has a non-zero balance.</summary>
    public List<AccumulatorTotalEntry> Totals { get; set; } = new();

    /// <summary>
    /// What each counted claim contributes to <see cref="Totals"/> (deductible and OOP
    /// entries; the totals are their sums). The Redis accumulator cache journals the
    /// rebuild per claim from these, so a later commit (a replacement, a re-commit) can
    /// undo exactly what the rebuild put in for that claim.
    /// </summary>
    public List<ClaimAccumulatorTotals> Claims { get; set; } = new();
}

/// <summary>One claim's contribution to <see cref="AccumulatorTotalsResponse.Totals"/>.</summary>
public class ClaimAccumulatorTotals
{
    public string ClaimId { get; set; } = string.Empty;
    public List<AccumulatorTotalEntry> Totals { get; set; } = new();
}

/// <summary>
/// One counted claim row for <c>GET /api/claims/accumulator-totals</c>, and the shared
/// rule (Mongo and Cosmos) that turns rows into totals and per-claim contributions.
/// </summary>
public sealed record AccumulatorTotalsRow(
    string ClaimId, string? NetworkTier, decimal Deductible, decimal Coinsurance, decimal Copay, decimal Oop)
{
    public static AccumulatorTotalsResponse Build(IEnumerable<AccumulatorTotalsRow> rows, string scope)
    {
        var deductibleType = scope == "Family" ? "FamilyDeductible" : "IndividualDeductible";
        var oopType = scope == "Family" ? "FamilyOutOfPocketMax" : "IndividualOutOfPocketMax";
        var deductible = new Dictionary<string, decimal>();
        var oop = new Dictionary<string, decimal>();
        var coinsurance = new Dictionary<string, decimal>();
        var copay = new Dictionary<string, decimal>();
        var perClaim = new Dictionary<string, ClaimAccumulatorTotals>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var tier = row.NetworkTier ?? "InNetwork";
            deductible[tier] = deductible.GetValueOrDefault(tier) + row.Deductible;
            oop[tier] = oop.GetValueOrDefault(tier) + row.Oop;
            coinsurance[tier] = coinsurance.GetValueOrDefault(tier) + row.Coinsurance;
            copay[tier] = copay.GetValueOrDefault(tier) + row.Copay;

            if (!perClaim.TryGetValue(row.ClaimId, out var claim))
                perClaim[row.ClaimId] = claim = new ClaimAccumulatorTotals { ClaimId = row.ClaimId };
            if (row.Deductible > 0)
                claim.Totals.Add(new AccumulatorTotalEntry { AccumulatorType = deductibleType, NetworkTier = tier, AccumulatedAmount = row.Deductible });
            if (row.Oop > 0)
                claim.Totals.Add(new AccumulatorTotalEntry { AccumulatorType = oopType, NetworkTier = tier, AccumulatedAmount = row.Oop });
        }

        var totals = new List<AccumulatorTotalEntry>();
        foreach (var (tier, amount) in deductible)
            if (amount > 0) totals.Add(new AccumulatorTotalEntry { AccumulatorType = deductibleType, NetworkTier = tier, AccumulatedAmount = amount });
        foreach (var (tier, amount) in oop)
            if (amount > 0) totals.Add(new AccumulatorTotalEntry { AccumulatorType = oopType, NetworkTier = tier, AccumulatedAmount = amount });
        // Coinsurance and copay are part of OOP; surfaced for the portal's breakdown.
        foreach (var (tier, amount) in coinsurance)
            if (amount > 0) totals.Add(new AccumulatorTotalEntry { AccumulatorType = "Coinsurance", NetworkTier = tier, AccumulatedAmount = amount });
        foreach (var (tier, amount) in copay)
            if (amount > 0) totals.Add(new AccumulatorTotalEntry { AccumulatorType = "Copay", NetworkTier = tier, AccumulatedAmount = amount });

        return new AccumulatorTotalsResponse
        {
            Totals = totals,
            Claims = perClaim.Values.Where(c => c.Totals.Count > 0).OrderBy(c => c.ClaimId, StringComparer.Ordinal).ToList(),
        };
    }
}

/// <summary>One aggregated accumulator bucket.</summary>
public class AccumulatorTotalEntry
{
    /// <summary>Matches <c>AccumulatorType</c> enum names in the benefit engine.</summary>
    public string AccumulatorType { get; set; } = string.Empty;

    /// <summary>Matches <c>NetworkTier</c> enum names: InNetwork, OutOfNetwork, OutOfArea.</summary>
    public string NetworkTier { get; set; } = string.Empty;

    /// <summary>Total accumulated amount (e.g. total deductible applied this plan year).</summary>
    public decimal AccumulatedAmount { get; set; }
}

/// <summary>
/// Portal claim search request body (POST /api/claims/search)
/// </summary>
public class ClaimSearchBody
{
    public string? RunId { get; set; }
    public string? ClaimNumber { get; set; }
    public string? MemberId { get; set; }
    public string? MemberName { get; set; }
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ClaimType { get; set; }
    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }
    public string? Status { get; set; }
    public string? AuthorizationNumber { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? SortBy { get; set; }
    public string? SortOrder { get; set; }
}

/// <summary>
/// Claims summary statistics
/// </summary>
public class ClaimsSummary
{
    public int TotalClaims { get; set; }
    public int ApprovedClaims { get; set; }
    public int DeniedClaims { get; set; }
    public int PendedClaims { get; set; }
    public int PaidClaims { get; set; }
    public decimal TotalChargeAmount { get; set; }
    public decimal TotalAllowedAmount { get; set; }
    public decimal TotalPaidAmount { get; set; }
    public decimal AverageProcessingDays { get; set; }
    public decimal ApprovalRate { get; set; }
}

/// <summary>
/// One prior claim that the duplicate-claim stage matched against this claim,
/// returned by <c>GET /api/claims/{id}/duplicate-matches</c> for the
/// examiner's "Possible duplicates" panel. Built from
/// <see cref="PendDetails.DuplicateFindings"/> plus a tenant-scoped read of
/// the matched claim. Carries only claim-level fields the claim-detail view
/// already shows (claim number, service dates, billed amount, status); no
/// member or provider demographics.
/// </summary>
public class ClaimDuplicateMatch
{
    /// <summary>Per-version id of the matched prior claim (link target).</summary>
    public string MatchedClaimId { get; set; } = string.Empty;

    /// <summary>Claim number of the matched prior claim.</summary>
    public string? MatchedClaimNumber { get; set; }

    /// <summary>
    /// "Exact" when any flagged line is an exact duplicate of this claim,
    /// otherwise "Suspect".
    /// </summary>
    public string MatchType { get; set; } = string.Empty;

    /// <summary>
    /// False when the matched claim could not be read (e.g. purged or not
    /// visible to this tenant); the summary fields below are then null.
    /// </summary>
    public bool MatchedClaimFound { get; set; }

    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }
    public decimal? BilledAmount { get; set; }
    public string? Status { get; set; }

    /// <summary>Fields that matched on every flagged line against this claim.</summary>
    public List<string> MatchedFields { get; set; } = new();

    /// <summary>Per-line pairings that produced this match, in line order.</summary>
    public List<ClaimDuplicateLineMatch> Lines { get; set; } = new();
}

/// <summary>One service line on this claim paired with a line on the matched claim.</summary>
public class ClaimDuplicateLineMatch
{
    public int LineNumber { get; set; }
    public int? MatchedLineNumber { get; set; }

    /// <summary>"Exact" or "Suspect".</summary>
    public string MatchType { get; set; } = string.Empty;

    /// <summary>DUP001 (exact) or DUP002 (suspect).</summary>
    public string RuleId { get; set; } = string.Empty;

    public List<string> MatchedFields { get; set; } = new();
    public string? Message { get; set; }
}
