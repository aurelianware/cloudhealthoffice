namespace CloudHealthOffice.Portal.Services;

// Premium Billing
/// <summary>
/// premium-billing-service (<c>Services:BillingService</c>, e.g.
/// <c>http://premium-billing-service:8080/api</c>). Refusals throw
/// <see cref="BillingApiException"/> with the service's reason; an unreachable
/// service throws <see cref="ServiceUnavailableException"/>.
/// </summary>
public interface IPremiumBillingService
{
    // Billing runs: reads billing:read, writes billing:run.
    Task<List<BillingRun>> GetBillingRunsAsync(DateTime? from = null, DateTime? to = null);
    Task<BillingRun?> GetBillingRunAsync(string id);
    Task<BillingRun> CreateBillingRunAsync(CreateBillingRunRequest request);
    Task<BillingRun> ExecuteBillingRunAsync(string id);
    Task CancelBillingRunAsync(string id);

    // Invoices: reads billing:read; payment and void finance:write; mark sent billing:run.
    Task<List<PremiumInvoice>> SearchInvoicesAsync(string? groupNumber = null, string? status = null,
        DateTime? periodFrom = null, DateTime? periodTo = null, int page = 1, int pageSize = 50);
    Task<PremiumInvoice?> GetInvoiceAsync(string id);
    Task<PremiumInvoice> RecordPaymentAsync(string invoiceId, RecordPremiumPaymentRequest request);
    Task<PremiumInvoice> VoidInvoiceAsync(string invoiceId, string reason);
    Task<PremiumInvoice> MarkInvoiceSentAsync(string invoiceId);
    Task<PremiumAgingReport> GetAgingReportAsync();

    /// <summary>finance:write. Returns the result for 200 and for 502 (some suspension failed).</summary>
    Task<DelinquencyRunResult> ProcessDelinquenciesAsync();

    // EFT: reads billing:read or payments:read; releases payments:approve (maker-checker).
    Task<List<EftDraft>> GetDraftsByInvoiceAsync(string invoiceId);
    Task<List<EftDraft>> GetDraftsForInvoicesAsync(IEnumerable<string> invoiceIds);
    Task<EftDraft> InitiateDraftAsync(InitiateEftDraftRequest request);
    Task<BatchEftResult> InitiateBatchDraftsAsync(InitiateBatchEftRequest request);
    /// <summary>The service sends the file to the bank; this is the masked summary and receipt.</summary>
    Task<NachaFileResult> GenerateNachaFileAsync();

    /// <summary>NACHA files that did not reach the bank (awaiting retrieval). Never the file.</summary>
    Task<List<NachaHeldFile>> GetHeldNachaFilesAsync();

    /// <summary>payments:approve, and not the user who released it.</summary>
    Task<NachaFileResult> RetryNachaTransmissionAsync(string fileReference);

    /// <summary>
    /// Member-scoped premium rollup consumed by the portal Member Details
    /// dialog (Premium tab). Returns null if the member has no invoices.
    /// </summary>
    Task<MemberPremiumSummary?> GetMemberPremiumSummaryAsync(string memberId);
}
