using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace PaymentService.Models;

/// <summary>
/// Represents an 835 Electronic Remittance Advice (ERA) payment transaction
/// Tracks claim payments, adjustments, and remittance details from payers
/// </summary>
public class Payment
{
    /// <summary>
    /// Multi-tenant partition key (set by repository from request tenant context)
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique payment identifier (Cosmos DB document id)
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Check/EFT number from payer
    /// 835: TRN02 (2000 level)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string CheckNumber { get; set; } = string.Empty;

    /// <summary>
    /// Payment method (CHK=Check, ACH=EFT, NON=Non-payment)
    /// 835: BPR01
    /// </summary>
    [Required]
    [StringLength(3)]
    public string PaymentMethod { get; set; } = "ACH";

    /// <summary>
    /// Total payment amount for this remittance
    /// 835: BPR02
    /// </summary>
    [Required]
    public decimal TotalPaymentAmount { get; set; }

    /// <summary>
    /// Payment date (when funds are transferred)
    /// 835: BPR16 (CCYYMMDD)
    /// </summary>
    [Required]
    public DateTime PaymentDate { get; set; }

    /// <summary>
    /// Payer name (health plan that sent payment)
    /// 835: NM103 (1000A)
    /// </summary>
    [Required]
    [StringLength(300)]
    public string PayerName { get; set; } = string.Empty;

    /// <summary>
    /// Payer identifier (from trading partners)
    /// </summary>
    [StringLength(50)]
    public string? PayerId { get; set; }

    /// <summary>
    /// Payee name (provider/facility receiving payment)
    /// 835: NM103 (1000B)
    /// </summary>
    [Required]
    [StringLength(300)]
    public string PayeeName { get; set; } = string.Empty;

    /// <summary>
    /// Payee NPI
    /// 835: NM109 (1000B)
    /// </summary>
    [StringLength(10)]
    public string? PayeeNPI { get; set; }

    /// <summary>
    /// Identifier of the trading partner this payment routes to. Used by
    /// <c>BatchEraGeneratorService</c> (5.10) to group N payments into
    /// per-partner 835 envelopes. Null when the payment was created by
    /// a flow that doesn't carry trading-partner context (legacy
    /// per-payment generation, manual posting tools).
    /// </summary>
    [StringLength(100)]
    public string? TradingPartnerId { get; set; }

    /// <summary>
    /// Individual claim payment details
    /// 835: 2100 loop (one per claim)
    /// </summary>
    public List<ClaimPayment> ClaimPayments { get; set; } = new();

    /// <summary>
    /// PLB (Provider Level Adjustments) - adjustments not tied to specific claims
    /// 835: PLB segment
    /// </summary>
    public List<ProviderAdjustment> ProviderAdjustments { get; set; } = new();

    /// <summary>
    /// Status of payment processing
    /// </summary>
    [Required]
    public PaymentStatus Status { get; set; } = PaymentStatus.Received;

    /// <summary>
    /// Raw 835 EDI file reference (blob storage path)
    /// </summary>
    public string? RawEdiFileUrl { get; set; }

    /// <summary>
    /// Timestamp when ERA was received
    /// </summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when payment was posted to accounts
    /// </summary>
    public DateTime? PostedAt { get; set; }

    /// <summary>
    /// Timestamp when payment was reconciled
    /// </summary>
    public DateTime? ReconciledAt { get; set; }

    /// <summary>
    /// User/system that posted the payment
    /// </summary>
    public string? PostedBy { get; set; }

    /// <summary>
    /// Notes about payment or exceptions
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Trace numbers for tracking
    /// </summary>
    public List<string> TraceNumbers { get; set; } = new();

    /// <summary>
    /// True when the payment record is the negative-amount reversal of a
    /// prior positive payment (5.12b ReversalRun). Drives downstream
    /// signaling: <see cref="EraEnvelopeRecord.ReversalRunId"/> is set in
    /// place of <see cref="EraEnvelopeRecord.PaymentRunId"/>, and CLP02
    /// status code "22" (Reversal of Previous Payment) is set on the
    /// individual <see cref="ClaimPayment.ClaimStatusCode"/>s by
    /// <c>ReversalRunService</c>. Default false; only ReversalRunService
    /// sets true.
    /// </summary>
    public bool IsReversal { get; set; } = false;
}

/// <summary>
/// Individual claim payment details within an 835
/// 835: 2100 loop
/// </summary>
public class ClaimPayment
{
    /// <summary>
    /// Original claim ID (links to Claims Service)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>
    /// Patient control number (original claim number submitted)
    /// 835: CLP01
    /// </summary>
    [Required]
    [StringLength(50)]
    public string PatientControlNumber { get; set; } = string.Empty;

    /// <summary>
    /// Claim status code (1=Processed, 2=Suspended, 3=Denied, 4=Pended, etc.)
    /// 835: CLP02
    /// </summary>
    [Required]
    [StringLength(2)]
    public string ClaimStatusCode { get; set; } = string.Empty;

    /// <summary>
    /// Total claim charge amount (provider billed)
    /// 835: CLP03
    /// </summary>
    public decimal ChargeAmount { get; set; }

    /// <summary>
    /// Total claim payment amount (payer allowed and paid)
    /// 835: CLP04
    /// </summary>
    public decimal PaymentAmount { get; set; }

    /// <summary>
    /// Patient responsibility amount
    /// 835: CLP05
    /// </summary>
    public decimal PatientResponsibilityAmount { get; set; }

    /// <summary>
    /// Payer claim control number (payer's internal ICN)
    /// 835: CLP07
    /// </summary>
    [StringLength(50)]
    public string? PayerClaimControlNumber { get; set; }

    /// <summary>
    /// Member ID on the claim
    /// </summary>
    [StringLength(50)]
    public string? MemberId { get; set; }

    /// <summary>
    /// Service line payments (itemized)
    /// 835: 2110 loop
    /// </summary>
    public List<ServiceLinePayment> ServiceLines { get; set; } = new();

    /// <summary>
    /// Claim-level adjustments
    /// 835: CAS segments at 2100 level
    /// </summary>
    public List<ClaimAdjustment> ClaimAdjustments { get; set; } = new();

    /// <summary>
    /// Date claim was received by payer
    /// 835: DTP*050
    /// </summary>
    public DateTime? ClaimReceivedDate { get; set; }

    /// <summary>
    /// Rendering provider NPI
    /// 835: NM109 (2100 NM1*82)
    /// </summary>
    [StringLength(10)]
    public string? RenderingProviderNPI { get; set; }
}

/// <summary>
/// Service line payment within a claim
/// 835: 2110 loop
/// </summary>
public class ServiceLinePayment
{
    /// <summary>
    /// Service line number (sequential)
    /// 835: SVC01 (composite, first element)
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// Procedure code (CPT/HCPCS)
    /// 835: SVC01-2
    /// </summary>
    [Required]
    [StringLength(10)]
    public string ProcedureCode { get; set; } = string.Empty;

    /// <summary>
    /// Line charge amount (provider billed)
    /// 835: SVC02
    /// </summary>
    public decimal ChargeAmount { get; set; }

    /// <summary>
    /// Line payment amount (payer allowed and paid)
    /// 835: SVC03
    /// </summary>
    public decimal PaymentAmount { get; set; }

    /// <summary>
    /// Revenue code (for facility claims)
    /// 835: SVC01-4
    /// </summary>
    [StringLength(4)]
    public string? RevenueCode { get; set; }

    /// <summary>
    /// Units of service
    /// 835: SVC05
    /// </summary>
    public decimal Units { get; set; }

    /// <summary>
    /// Service date (from)
    /// 835: DTM*472
    /// </summary>
    public DateTime? ServiceDateFrom { get; set; }

    /// <summary>
    /// Service date (to)
    /// 835: DTM*473
    /// </summary>
    public DateTime? ServiceDateTo { get; set; }

    /// <summary>
    /// Service line adjustments (denials, contractual, etc.)
    /// 835: CAS segments at 2110 level
    /// </summary>
    public List<ServiceLineAdjustment> Adjustments { get; set; } = new();
}

/// <summary>
/// Claim-level adjustment (CO, PR, OA, PI, etc.)
/// 835: CAS segment
/// </summary>
public class ClaimAdjustment
{
    /// <summary>
    /// Group code (CO=Contractual, PR=Patient Responsibility, OA=Other, PI=Payer Initiated)
    /// 835: CAS01
    /// </summary>
    [Required]
    [StringLength(2)]
    public string GroupCode { get; set; } = string.Empty;

    /// <summary>
    /// Reason code (CARC - Claim Adjustment Reason Code)
    /// 835: CAS02
    /// </summary>
    [Required]
    [StringLength(5)]
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>
    /// Adjustment amount
    /// 835: CAS03
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Human-readable reason description
    /// </summary>
    public string? ReasonDescription { get; set; }
}

/// <summary>
/// Service line adjustment
/// 835: CAS segment at 2110 level
/// </summary>
public class ServiceLineAdjustment
{
    /// <summary>
    /// Group code (CO, PR, OA, PI)
    /// 835: CAS01
    /// </summary>
    [Required]
    [StringLength(2)]
    public string GroupCode { get; set; } = string.Empty;

    /// <summary>
    /// Reason code (CARC)
    /// 835: CAS02
    /// </summary>
    [Required]
    [StringLength(5)]
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>
    /// Adjustment amount
    /// 835: CAS03
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Quantity adjusted (optional)
    /// 835: CAS05
    /// </summary>
    public decimal? Quantity { get; set; }

    /// <summary>
    /// RARC (Remittance Advice Remark Code) for additional context
    /// </summary>
    [StringLength(10)]
    public string? RemarkCode { get; set; }

    /// <summary>
    /// Human-readable reason description
    /// </summary>
    public string? ReasonDescription { get; set; }
}

/// <summary>
/// Provider-level adjustment (not tied to specific claim)
/// 835: PLB segment
/// </summary>
public class ProviderAdjustment
{
    /// <summary>
    /// Adjustment identifier
    /// 835: PLB03-1
    /// </summary>
    [Required]
    [StringLength(10)]
    public string AdjustmentIdentifier { get; set; } = string.Empty;

    /// <summary>
    /// Reference identification (check number, invoice, etc.)
    /// 835: PLB03-2
    /// </summary>
    [StringLength(50)]
    public string? ReferenceIdentification { get; set; }

    /// <summary>
    /// Adjustment amount
    /// 835: PLB04
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Fiscal period end date
    /// 835: PLB02 (CCYYMMDD)
    /// </summary>
    public DateTime? FiscalPeriodEnd { get; set; }

    /// <summary>
    /// Description of adjustment
    /// </summary>
    public string? Description { get; set; }
}

public enum PaymentStatus
{
    Received,       // 835 received and parsed
    Validated,      // Payment validated against claims
    Posted,         // Posted to patient accounts
    Reconciled,     // Reconciled with bank deposit
    Exception       // Requires manual review
}

/// <summary>
/// Payment summary statistics
/// </summary>
public class PaymentsSummary
{
    public int TotalPayments { get; set; }
    public decimal TotalPaymentAmount { get; set; }
    public int TotalClaims { get; set; }
    public int PostedPayments { get; set; }
    public int UnpostedPayments { get; set; }
    public int ExceptionPayments { get; set; }
    public Dictionary<string, decimal> PaymentsByPayer { get; set; } = new();
    public Dictionary<string, int> ClaimsByStatus { get; set; } = new();
}
