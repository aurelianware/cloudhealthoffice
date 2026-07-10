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
/// Accumulator totals response — aggregated cost-share amounts per bucket.
/// Returned by GET /api/claims/accumulator-totals; consumed by the Redis
/// accumulator service on a cache miss to rebuild from claim history.
/// </summary>
public class AccumulatorTotalsResponse
{
    /// <summary>One entry per (AccumulatorType × NetworkTier) combination that has a non-zero balance.</summary>
    public List<AccumulatorTotalEntry> Totals { get; set; } = new();
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
