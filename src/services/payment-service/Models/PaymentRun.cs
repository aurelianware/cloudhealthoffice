using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PaymentService.Models;

/// <summary>
/// Represents a payment run batch job
/// Groups approved claims for payment processing
/// </summary>
public class PaymentRun
{
    /// <summary>
    /// Multi-tenant partition key
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique payment run identifier
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Payment run number (user-facing)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string PaymentRunNumber { get; set; } = string.Empty;

    /// <summary>
    /// Payment run description
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Payment run status
    /// </summary>
    [Required]
    public PaymentRunStatus Status { get; set; } = PaymentRunStatus.Pending;

    /// <summary>
    /// Filter criteria used for this payment run
    /// </summary>
    public PaymentRunCriteria Criteria { get; set; } = new();

    /// <summary>
    /// Generated payments in this run
    /// </summary>
    public List<string> PaymentIds { get; set; } = new();

    /// <summary>
    /// Generated <c>EraEnvelopeRecord</c> ids — one per trading partner
    /// in this run (5.10 batched 835 generation). Empty for runs that
    /// pre-date 5.10 or whose claims didn't resolve to any trading
    /// partner.
    /// </summary>
    public List<string> EraEnvelopeIds { get; set; } = new();

    /// <summary>
    /// Claims included in this payment run
    /// </summary>
    public List<string> ClaimIds { get; set; } = new();

    /// <summary>
    /// Total number of claims processed
    /// </summary>
    public int TotalClaims { get; set; }

    /// <summary>
    /// Total payment amount
    /// </summary>
    public decimal TotalPaymentAmount { get; set; }

    /// <summary>
    /// Check number range assigned
    /// </summary>
    public string? CheckNumberStart { get; set; }

    /// <summary>
    /// Check number range end
    /// </summary>
    public string? CheckNumberEnd { get; set; }

    /// <summary>
    /// Next check number to assign
    /// </summary>
    public int NextCheckNumber { get; set; }

    /// <summary>
    /// Payment run created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who created the payment run (token subject; a service client id when
    /// a service created it). The maker in maker-checker.
    /// </summary>
    [StringLength(100)]
    public string? CreatedBy { get; set; }

    /// <summary>User who executed (released) the payment run, from the token. Never the creator.</summary>
    [StringLength(100)]
    public string? ExecutedBy { get; set; }

    /// <summary>User or service that cancelled the run, from the token.</summary>
    [StringLength(100)]
    public string? CancelledBy { get; set; }

    /// <summary>When the run was cancelled.</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>
    /// Payment run execution started
    /// </summary>
    public DateTime? ExecutionStartedAt { get; set; }

    /// <summary>
    /// Payment run execution completed
    /// </summary>
    public DateTime? ExecutionCompletedAt { get; set; }

    /// <summary>
    /// Execution duration in seconds
    /// </summary>
    public double? ExecutionDurationSeconds { get; set; }

    /// <summary>
    /// Error messages if any
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Warnings during execution
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Claims this run paid that claims-service has not finalized yet (the
    /// finalize call failed, or no trading partner resolved so no 835 was
    /// emitted). Their payments are <c>PaidPendingFinalize</c>. They are never
    /// paid again; POST /api/paymentruns/{id}/finalize (or the next run that
    /// sees them) retries the finalize without a new payment.
    /// </summary>
    public List<string> PendingFinalizeClaimIds { get; set; } = new();

    /// <summary>
    /// Claims returned by claims-service that this run did not pay because
    /// payment-service already holds a payment for them, or another run holds
    /// their payment reservation.
    /// </summary>
    public List<string> AlreadyPaidClaimIds { get; set; } = new();

    /// <summary>
    /// Claims not paid because their pay-to / billing provider has no trading
    /// partner, so no 835 could be sent. They stay Approved in claims-service
    /// and are picked up by a run once a partner is configured.
    /// </summary>
    public List<string> NeedsTradingPartnerClaimIds { get; set; } = new();

    /// <summary>
    /// Claims not paid because claims-service returned them without a plan-paid
    /// amount (<c>adjudicationResult.payerPayment</c>; no adjudication result).
    /// A claim is never paid at its billed charges or allowed amount: it is not
    /// reserved, stays Approved in claims-service, and is picked up by a run
    /// once it carries an adjudication result.
    /// </summary>
    public List<string> MissingPlanPaidAmountClaimIds { get; set; } = new();

    /// <summary>
    /// Claims not paid because their plan-paid amount is negative. A plan
    /// payment is never negative (a recoupment is a reversal run); zero is paid
    /// as a zero-pay remittance. They stay Approved in claims-service.
    /// </summary>
    public List<string> NegativePlanPaidClaimIds { get; set; } = new();

    /// <summary>
    /// Claims not paid because their service-line paid amounts (SVC03) do not
    /// add up to their plan-paid amount (CLP04), so their 835 would not balance.
    /// A line with no paid amount counts as 0. A claim with no service lines
    /// (claim-level-only adjudication) is listed here too. They stay Approved in
    /// claims-service and are picked up once their line amounts are corrected.
    /// </summary>
    public List<string> UnbalancedServiceLineClaimIds { get; set; } = new();

    /// <summary>
    /// Denied claims this run remitted: each appears in this run's 835 as a
    /// zero-pay claim (CLP02 = 4, CLP04 = 0, CAS with its denial CARC, MOA with
    /// its RARCs). No payment or reservation is created for them. A denied
    /// claim is remitted once: one already listed in a (non-reversal) 835, or
    /// with a payment, is not remitted again.
    /// </summary>
    public List<string> RemittedDeniedClaimIds { get; set; } = new();

    /// <summary>
    /// Denied claims not remitted because claims-service returned them without
    /// a denial reason (<c>adjudicationResult.denialReasonCode</c>) or any
    /// adjustment reason: an 835 denial needs a CARC, and none is made up.
    /// They are picked up once their adjudication carries one.
    /// </summary>
    public List<string> DeniedWithoutReasonClaimIds { get; set; } = new();

    /// <summary>
    /// Claims whose payment reservation this run held, and which were released
    /// after it failed or was cancelled without paying them: automatically (no
    /// payment and no 835 in payment-service) or by a second approver. A later
    /// run may pay them. The audit log (PaymentReservationAudit) has who and why.
    /// </summary>
    public List<string> ReleasedReservationClaimIds { get; set; } = new();

    /// <summary>
    /// Reservations this run holds that reconciliation could not release safely
    /// (a payment or 835 exists, the run is stuck Running, or the state cannot
    /// be classified). A person releases them with
    /// POST /api/paymentruns/{id}/reservations/{claimId}/release.
    /// </summary>
    public List<ReservationAttention> ReservationsNeedingAttention { get; set; } = new();

    /// <summary>
    /// Payments of an ACH run paid by check because their payee has no approved
    /// EFT account in provider-service (none approved, EFT not enabled, or no
    /// payee TIN). Decided at execution, so the payment and its 835 BPR04 say
    /// CHK; extended when the EFT file finds an account gone since.
    /// </summary>
    public List<CheckFallbackPayment> CheckFallbacks { get; set; } = new();

    /// <summary>
    /// Provider receivables this run recovered from its payments (positive PLB
    /// FB/WO offsets); <see cref="TotalPaymentAmount"/> is already net of them.
    /// </summary>
    public List<PaymentRunReceivableRecovery> ReceivableRecoveries { get; set; } = new();

    /// <summary>Sum of <see cref="ReceivableRecoveries"/>.</summary>
    public decimal ReceivableRecoveredAmount { get; set; }

    /// <summary>
    /// The run's NACHA CCD+ credit file, pinned at first generation
    /// (POST /api/paymentruns/{id}/eft-file). Null until generated.
    /// </summary>
    public PaymentRunEftFile? EftFile { get; set; }

    /// <summary>
    /// Payment method for this run (ACH, Check)
    /// </summary>
    [StringLength(10)]
    public string PaymentMethod { get; set; } = "ACH";

    /// <summary>
    /// Payment date
    /// </summary>
    public DateTime PaymentDate { get; set; } = DateTime.UtcNow.AddDays(3);

    /// <summary>
    /// Scheduled run (vs manual)
    /// </summary>
    public bool IsScheduled { get; set; } = false;

    /// <summary>
    /// Cron expression if scheduled
    /// </summary>
    public string? ScheduleExpression { get; set; }
}

/// <summary>
/// Payment run filter criteria
/// </summary>
public class PaymentRunCriteria
{
    /// <summary>
    /// Line of Business filter
    /// </summary>
    public LineOfBusiness? LineOfBusiness { get; set; }

    /// <summary>
    /// Provider NPI filter (pay-to provider)
    /// </summary>
    [StringLength(10)]
    public string? ProviderNPI { get; set; }

    /// <summary>
    /// Service date from
    /// </summary>
    public DateTime? ServiceDateFrom { get; set; }

    /// <summary>
    /// Service date to
    /// </summary>
    public DateTime? ServiceDateTo { get; set; }

    /// <summary>
    /// Claim submission date from
    /// </summary>
    public DateTime? SubmissionDateFrom { get; set; }

    /// <summary>
    /// Claim submission date to
    /// </summary>
    public DateTime? SubmissionDateTo { get; set; }

    /// <summary>
    /// Minimum claim amount
    /// </summary>
    public decimal? MinClaimAmount { get; set; }

    /// <summary>
    /// Maximum claim amount
    /// </summary>
    public decimal? MaxClaimAmount { get; set; }

    /// <summary>
    /// Claim IDs to include (manual selection)
    /// </summary>
    public List<string> IncludeClaimIds { get; set; } = new();

    /// <summary>
    /// Claim IDs to exclude
    /// </summary>
    public List<string> ExcludeClaimIds { get; set; } = new();

    /// <summary>
    /// Only include claims with specific member IDs
    /// </summary>
    public List<string> MemberIds { get; set; } = new();

    /// <summary>
    /// Group payments by provider
    /// </summary>
    public bool GroupByProvider { get; set; } = true;

    /// <summary>
    /// Maximum claims per payment
    /// </summary>
    public int? MaxClaimsPerPayment { get; set; }

    /// <summary>
    /// Also remit denied claims (claims-service status Denied) that have not
    /// been remitted yet, as zero-pay claims in the run's 835s. Default true:
    /// providers must receive a remittance for denials. The same criteria
    /// (dates, provider, amounts, include/exclude lists) select them.
    /// </summary>
    public bool IncludeDeniedClaims { get; set; } = true;
}

/// <summary>
/// Claim status, mirrored value for value from claims-service's
/// <c>ClaimsService.Models.ClaimStatus</c>: claims-service serializes it as a
/// number, so the numbers must match (they previously did not, and 5 —
/// claims-service Approved — read here as Denied).
/// </summary>
public enum ClaimStatus
{
    Submitted = 1,
    Received = 2,
    InAdjudication = 3,
    Pended = 4,
    Approved = 5,
    Denied = 6,
    Paid = 7,
    Voided = 8,
    PartiallyPaid = 9
}

/// <summary>
/// Line of Business enumeration
/// </summary>
public enum LineOfBusiness
{
    Commercial,
    Medicare,
    Medicaid,
    Marketplace
}

public enum PaymentRunStatus
{
    Pending,      // Created but not executed
    Running,      // Currently executing
    Completed,    // Successfully completed
    Failed,       // Failed with errors
    Cancelled     // Manually cancelled
}
