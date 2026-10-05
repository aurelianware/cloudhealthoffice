using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

// Wire shapes of premium-billing-service (api/v1/billing-runs, premium-invoices,
// eft) and sponsor-service's sponsor bank-account endpoints
// (api/v1/sponsors/{group}/bank-account...). Both services send camelCase
// properties and enum names as strings, so enums are strings here.
//
// Bank details: no type here has a full routing or account number property.
// The masked reads only carry the last 4; the portal could not show more even
// if a response carried it. NACHA files go from the services straight to the
// bank: the portal gets a masked summary and a receipt, never a file.

// ── Billing runs ──────────────────────────────────────────────────────────

public class BillingRun
{
    public string Id { get; set; } = string.Empty;
    public string BillingRunNumber { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime BillingPeriod { get; set; }
    /// <summary>Pending, Running, Completed, Failed, Cancelled.</summary>
    public string Status { get; set; } = string.Empty;
    public BillingRunCriteria Criteria { get; set; } = new();
    public List<string> InvoiceIds { get; set; } = new();
    public int TotalInvoices { get; set; }
    public decimal TotalPremiumAmount { get; set; }
    public decimal TotalAdjustmentAmount { get; set; }
    public int TotalMembers { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public bool CreatedByIsService { get; set; }
    public string? ExecutedBy { get; set; }
    public bool ExecutedByIsService { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? ExecutionStartedAt { get; set; }
    public DateTime? ExecutionCompletedAt { get; set; }
    public double? ExecutionDurationSeconds { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class BillingRunCriteria
{
    public List<string> GroupNumbers { get; set; } = new();
    /// <summary>Commercial, Medicare, Medicaid, Exchange, TRICARE, VA; null for all.</summary>
    public string? LineOfBusiness { get; set; }
    /// <summary>Monthly, Quarterly, SemiAnnually, Annual; null for all.</summary>
    public string? BillingFrequency { get; set; }
}

/// <summary>Body of POST billing-runs. The creator comes from the token, never the body.</summary>
public class CreateBillingRunRequest
{
    public DateTime BillingPeriod { get; set; }
    public BillingRunCriteria Criteria { get; set; } = new();
    public string? Description { get; set; }
}

// ── Premium invoices ──────────────────────────────────────────────────────

public class PremiumInvoice
{
    public string Id { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string? BillingRunId { get; set; }
    public string GroupNumber { get; set; } = string.Empty;
    public string SponsorName { get; set; } = string.Empty;
    public DateTime BillingPeriodStart { get; set; }
    public DateTime BillingPeriodEnd { get; set; }
    /// <summary>Generated, Sent, PartiallyPaid, Paid, Overdue, Delinquent, Voided, WriteOff.</summary>
    public string Status { get; set; } = string.Empty;
    public DateTime DueDate { get; set; }
    public List<PremiumInvoiceLineItem> LineItems { get; set; } = new();
    public List<PremiumInvoiceAdjustment> Adjustments { get; set; } = new();
    public List<PremiumInvoicePayment> Payments { get; set; } = new();
    public decimal SubtotalPremium { get; set; }
    public decimal TotalAdjustments { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal BalanceDue { get; set; }
    public int MemberCount { get; set; }
    public int GracePeriodDays { get; set; }
    public DateTime? GracePeriodExpires { get; set; }
    public bool IsAptcSubsidized { get; set; }
    public decimal AptcMonthlyAmount { get; set; }
    public string GraceType { get; set; } = "Standard";
    public DateTime CreatedAt { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? LastUpdatedBy { get; set; }
    public SponsorSuspensionRecord? SponsorSuspension { get; set; }
}

public class PremiumInvoiceLineItem
{
    public string MemberId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string? CoverageId { get; set; }
    public string? PlanId { get; set; }
    public string? CoverageLevel { get; set; }
    public string? InsuranceLineCode { get; set; }
    public decimal SubscriberPremium { get; set; }
    public decimal EmployerContribution { get; set; }
    public decimal TotalPremium { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public decimal ProrationFactor { get; set; } = 1.0m;
    public bool IsRetroactive { get; set; }
    public string? AdjustmentReason { get; set; }
}

public class PremiumInvoiceAdjustment
{
    /// <summary>RetroAdd, RetroTerm, RateChange, Credit, Other.</summary>
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? RelatedMemberId { get; set; }
    public DateTime AdjustmentDate { get; set; }
}

public class PremiumInvoicePayment
{
    public string PaymentId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime PaymentDate { get; set; }
    public string? PaymentMethod { get; set; }
    public string? ReferenceNumber { get; set; }
    public DateTime ReceivedDate { get; set; }
    public string? RecordedBy { get; set; }
}

/// <summary>What happened when billing asked sponsor-service to suspend a delinquent invoice's sponsor.</summary>
public class SponsorSuspensionRecord
{
    /// <summary>Suspended or Failed (Failed is retried on every delinquency run).</summary>
    public string State { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime LastAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public string? LastAttemptBy { get; set; }
    public DateTime? SuspendedAt { get; set; }
}

/// <summary>Body of POST premium-invoices/{id}/payments.</summary>
public class RecordPremiumPaymentRequest
{
    public decimal Amount { get; set; }
    public DateTime PaymentDate { get; set; }
    public string? PaymentMethod { get; set; }
    public string? ReferenceNumber { get; set; }
}

public class PremiumAgingReport
{
    public decimal CurrentAmount { get; set; }
    public int CurrentCount { get; set; }
    public decimal ThirtyDayAmount { get; set; }
    public int ThirtyDayCount { get; set; }
    public decimal SixtyDayAmount { get; set; }
    public int SixtyDayCount { get; set; }
    public decimal NinetyPlusDayAmount { get; set; }
    public int NinetyPlusDayCount { get; set; }
    public decimal TotalOutstanding { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>
/// Result of POST premium-invoices/process-delinquencies. The service answers
/// 200 when every suspension succeeded and 502 (same body) when any failed.
/// </summary>
public class DelinquencyRunResult
{
    public int DelinquentCount { get; set; }
    public int SponsorsSuspended { get; set; }
    public int SuspensionRetries { get; set; }
    public List<SponsorSuspensionFailure> SuspensionFailures { get; set; } = new();
    public string? Message { get; set; }

    /// <summary>True when the service answered 502 (some suspension failed in sponsor-service).</summary>
    [JsonIgnore]
    public bool SuspensionsFailed { get; set; }
}

public class SponsorSuspensionFailure
{
    public string InvoiceId { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string? Error { get; set; }
}

// ── EFT / auto-debit ──────────────────────────────────────────────────────

public class EftDraft
{
    public string Id { get; set; } = string.Empty;
    public string InvoiceId { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    /// <summary>Nacha or StripeAch.</summary>
    public string Method { get; set; } = string.Empty;
    /// <summary>Pending, Submitted, Processing, Settled, Returned, Failed, Cancelled.</summary>
    public string Status { get; set; } = string.Empty;
    public string? TraceNumber { get; set; }
    public string? NachaFileReference { get; set; }
    public string? RoutingNumberLast4 { get; set; }
    public string? AccountNumberLast4 { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ExpectedSettlementDate { get; set; }
    public DateTime? SettledAt { get; set; }
    public string? ReturnCode { get; set; }
    public string? ReturnReason { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; }
    public string? InitiatedBy { get; set; }
    public string? LastUpdatedBy { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>Body of POST eft/drafts. The initiator comes from the token.</summary>
public class InitiateEftDraftRequest
{
    public string InvoiceId { get; set; } = string.Empty;
    /// <summary>Nacha or StripeAch; null uses the sponsor's preferred method.</summary>
    public string? Method { get; set; }
    public decimal? Amount { get; set; }
}

/// <summary>Body of POST eft/drafts/batch: a billing run, or specific invoices.</summary>
public class InitiateBatchEftRequest
{
    public string? BillingRunId { get; set; }
    public List<string> InvoiceIds { get; set; } = new();
    public string? Method { get; set; }
}

public class BatchEftResult
{
    public int TotalInvoices { get; set; }
    public int DraftsInitiated { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public decimal TotalAmount { get; set; }
    public List<string> DraftIds { get; set; } = new();
    public List<string> ErrorMessages { get; set; } = new();
    public NachaFileResult? NachaFile { get; set; }
    public List<EftAttentionItem> NeedsAttention { get; set; } = new();
}

/// <summary>
/// A NACHA file as the services report it: the services send the file to the
/// bank themselves, so this is a masked summary and a receipt. There is no
/// file content and no full number here.
/// </summary>
public class NachaFileResult
{
    public string FileReference { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalDebitAmount { get; set; }
    public decimal TotalCreditAmount { get; set; }
    public DateTime GeneratedAt { get; set; }
    /// <summary>Transmitted, AwaitingRetrieval, NotSent or DeliveryUnknown (may be at the bank: never re-send).</summary>
    public string TransmissionStatus { get; set; } = string.Empty;
    public string? TransmissionError { get; set; }
    public DateTime? HeldUntil { get; set; }
    public NachaTransmissionReceipt? Receipt { get; set; }
    public List<NachaEntrySummary> Entries { get; set; } = new();
    public List<EftAttentionItem> NeedsAttention { get; set; } = new();
}

/// <summary>One debit or credit in a NACHA file: who, last 4, amount.</summary>
public class NachaEntrySummary
{
    public string? DraftId { get; set; }
    public string? InvoiceId { get; set; }
    public string? GroupNumber { get; set; }
    public string? DisbursementId { get; set; }
    public string? StatementId { get; set; }
    public string? ProviderNPI { get; set; }
    public string? ProviderName { get; set; }
    public string? AccountHolderName { get; set; }
    public string? RoutingNumberLast4 { get; set; }
    public string? AccountNumberLast4 { get; set; }
    public decimal Amount { get; set; }
    public string? TraceNumber { get; set; }
}

/// <summary>The bank's drop received the file.</summary>
public class NachaTransmissionReceipt
{
    public string TenantId { get; set; } = string.Empty;
    public string FileReference { get; set; } = string.Empty;
    public string RemoteFileName { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public long ByteSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public decimal TotalDebitAmount { get; set; }
    public decimal TotalCreditAmount { get; set; }
    public DateTime TransmittedAt { get; set; }
    public string TransmittedBy { get; set; } = string.Empty;
    public string? RunId { get; set; }
    public string? BatchId { get; set; }
}

/// <summary>A NACHA file that did not reach the bank and waits (never the file).</summary>
public class NachaHeldFile
{
    public string FileReference { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>AwaitingRetrieval, Transmitting or DeliveryUnknown (may be at the bank: verify, never re-send).</summary>
    public string Status { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public decimal TotalDebitAmount { get; set; }
    public decimal TotalCreditAmount { get; set; }
    public string? RunId { get; set; }
    public string ReleasedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public string? LastAttemptBy { get; set; }
}

/// <summary>An invoice or draft that was not debited for a reason someone has to fix.</summary>
public class EftAttentionItem
{
    public string? InvoiceId { get; set; }
    public string? DraftId { get; set; }
    public string GroupNumber { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

// ── Sponsor bank accounts (sponsor-service, dual control) ─────────────────

/// <summary>
/// A sponsor bank account as the masked reads return it: last 4 only. There is
/// deliberately no full routing or account number property.
/// </summary>
public class SponsorBankAccountMasked
{
    public bool EftEnabled { get; set; }
    /// <summary>Nacha or StripeAch.</summary>
    public string? PreferredMethod { get; set; }
    /// <summary>Checking or Savings.</summary>
    public string? AccountType { get; set; }
    public string? AccountHolderName { get; set; }
    public string? RoutingNumberLast4 { get; set; }
    public string? AccountNumberLast4 { get; set; }
}

public class SponsorBankAccountChange
{
    public string Id { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    /// <summary>Pending, Approved, Rejected, Cancelled.</summary>
    public string Status { get; set; } = string.Empty;
    public SponsorBankAccountMasked? Proposed { get; set; }
    public bool NumbersCarriedOver { get; set; }
    public SponsorBankAccountMasked? PreviousAccount { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Reason { get; set; }
}

public class SponsorBankAccountView
{
    public string GroupNumber { get; set; } = string.Empty;
    /// <summary>The approved account premium billing debits; null when none is approved.</summary>
    public SponsorBankAccountMasked? Active { get; set; }
    public string? ActiveApprovedBy { get; set; }
    public DateTime? ActiveApprovedAt { get; set; }
    public SponsorBankAccountChange? Pending { get; set; }
}

/// <summary>
/// Body of POST sponsors/{group}/bank-account-changes: the account and
/// enrollment as they should be after approval. Leave both numbers empty to
/// keep the approved account's numbers.
/// </summary>
public class ProposeSponsorBankAccountRequest
{
    public bool EftEnabled { get; set; }
    public string? PreferredMethod { get; set; }
    public string? RoutingNumber { get; set; }
    public string? AccountNumber { get; set; }
    public string AccountType { get; set; } = "Checking";
    public string? AccountHolderName { get; set; }
}

/// <summary>How the portal shows a bank number: "••••1234", never more than the last 4.</summary>
public static class BankNumberMask
{
    public static string Masked(string? last4)
    {
        if (string.IsNullOrWhiteSpace(last4)) return "—";
        var tail = last4.Length > 4 ? last4[^4..] : last4;
        return "••••" + tail;
    }
}

// ── Errors ────────────────────────────────────────────────────────────────

/// <summary>
/// A premium-billing-service or sponsor-service refusal (4xx/5xx), with the
/// service's own explanation. <see cref="UserMessage"/> is what the pages show.
/// </summary>
public class BillingApiException : Exception
{
    public int StatusCode { get; }
    public string? Title { get; }
    public string? Detail { get; }

    /// <summary>The raw response body (the 502 from process-delinquencies carries its result here).</summary>
    public string? Body { get; init; }

    public BillingApiException(int statusCode, string? title, string? detail)
        : base(BuildMessage(statusCode, title, detail))
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
    }

    /// <summary>403 maker-checker refusal: the user prepared what they are trying to release or approve.</summary>
    public bool IsSeparationOfDuties =>
        StatusCode == 403 && string.Equals(Title, "Separation of duties", StringComparison.OrdinalIgnoreCase);

    /// <summary>409: someone changed the record since it was loaded.</summary>
    public bool IsStale => StatusCode == 409;

    public string UserMessage => Message;

    private static string BuildMessage(int statusCode, string? title, string? detail)
    {
        var reason = string.IsNullOrWhiteSpace(detail) ? title : detail;
        if (statusCode == 403 && string.Equals(title, "Separation of duties", StringComparison.OrdinalIgnoreCase))
            return "Separation of duties: you cannot approve or release something you prepared yourself. " +
                   "A different user with payments:approve (FinanceApprover) must do it." +
                   (string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail})");
        return statusCode switch
        {
            403 => "You do not have permission for this action." + (string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason})"),
            409 => "This record changed since you loaded it (someone else acted on it first). Reload and try again." +
                   (string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason})"),
            404 => reason ?? "Not found.",
            _ => reason ?? $"The request failed (HTTP {statusCode})."
        };
    }
}
