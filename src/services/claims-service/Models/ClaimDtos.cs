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
/// Remittance update request (835 transaction)
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
