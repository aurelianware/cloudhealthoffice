using System.Net.Http.Json;
using System.Net;

namespace CloudHealthOffice.Portal.Services;

// ── Premium Billing Service ────────────────────────────────────────────

public class PremiumBillingService : IPremiumBillingService
{
    internal const string ServiceName = "Premium Billing Service";
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PremiumBillingService> _logger;

    public PremiumBillingService(HttpClient httpClient, IConfiguration configuration, ILogger<PremiumBillingService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string Url(string path) => $"{_configuration["Services:BillingService"]}/v1/{path}";

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null)
        => BillingApiHttp.SendAsync<T>(_httpClient, _logger, ServiceName, method, Url(path), body);

    // ── Billing runs ──

    public async Task<List<BillingRun>> GetBillingRunsAsync(DateTime? from = null, DateTime? to = null)
    {
        var query = new List<string>();
        if (from.HasValue) query.Add($"from={from.Value:yyyy-MM-dd}");
        if (to.HasValue) query.Add($"to={to.Value:yyyy-MM-dd}");
        var path = "billing-runs" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return await SendAsync<List<BillingRun>?>(HttpMethod.Get, path) ?? new();
    }

    public async Task<BillingRun?> GetBillingRunAsync(string id)
    {
        try
        {
            return await SendAsync<BillingRun>(HttpMethod.Get, $"billing-runs/{Esc(id)}");
        }
        catch (BillingApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public Task<BillingRun> CreateBillingRunAsync(CreateBillingRunRequest request)
        => SendAsync<BillingRun>(HttpMethod.Post, "billing-runs", request);

    public Task<BillingRun> ExecuteBillingRunAsync(string id)
        => SendAsync<BillingRun>(HttpMethod.Post, $"billing-runs/{Esc(id)}/execute");

    public Task CancelBillingRunAsync(string id)
        => SendAsync<object?>(HttpMethod.Post, $"billing-runs/{Esc(id)}/cancel");

    // ── Invoices ──

    public async Task<List<PremiumInvoice>> SearchInvoicesAsync(string? groupNumber = null, string? status = null,
        DateTime? periodFrom = null, DateTime? periodTo = null, int page = 1, int pageSize = 50)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(groupNumber)) query.Add($"groupNumber={Esc(groupNumber.Trim())}");
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Esc(status)}");
        if (periodFrom.HasValue) query.Add($"periodFrom={periodFrom.Value:yyyy-MM-dd}");
        if (periodTo.HasValue) query.Add($"periodTo={periodTo.Value:yyyy-MM-dd}");
        return await SendAsync<List<PremiumInvoice>?>(HttpMethod.Get, "premium-invoices?" + string.Join("&", query)) ?? new();
    }

    public async Task<PremiumInvoice?> GetInvoiceAsync(string id)
    {
        try
        {
            return await SendAsync<PremiumInvoice>(HttpMethod.Get, $"premium-invoices/{Esc(id)}");
        }
        catch (BillingApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public Task<PremiumInvoice> RecordPaymentAsync(string invoiceId, RecordPremiumPaymentRequest request)
        => SendAsync<PremiumInvoice>(HttpMethod.Post, $"premium-invoices/{Esc(invoiceId)}/payments", request);

    public Task<PremiumInvoice> VoidInvoiceAsync(string invoiceId, string reason)
        => SendAsync<PremiumInvoice>(HttpMethod.Post, $"premium-invoices/{Esc(invoiceId)}/void", new { reason });

    public Task<PremiumInvoice> MarkInvoiceSentAsync(string invoiceId)
        => SendAsync<PremiumInvoice>(HttpMethod.Post, $"premium-invoices/{Esc(invoiceId)}/send");

    public async Task<PremiumAgingReport> GetAgingReportAsync()
        => await SendAsync<PremiumAgingReport?>(HttpMethod.Get, "premium-invoices/aging-report") ?? new();

    public async Task<DelinquencyRunResult> ProcessDelinquenciesAsync()
    {
        try
        {
            return await SendAsync<DelinquencyRunResult?>(HttpMethod.Post, "premium-invoices/process-delinquencies")
                   ?? new DelinquencyRunResult();
        }
        catch (BillingApiException ex) when (ex.StatusCode == 502 && !string.IsNullOrWhiteSpace(ex.Body))
        {
            // 502: invoices were marked delinquent but some sponsor suspensions
            // failed in sponsor-service; the body says which.
            var result = BillingApiHttp.TryDeserialize<DelinquencyRunResult>(ex.Body!);
            if (result == null) throw;
            result.SuspensionsFailed = true;
            return result;
        }
    }

    // ── EFT ──

    public async Task<List<EftDraft>> GetDraftsByInvoiceAsync(string invoiceId)
        => await SendAsync<List<EftDraft>?>(HttpMethod.Get, $"eft/drafts/invoice/{Esc(invoiceId)}") ?? new();

    public async Task<List<EftDraft>> GetDraftsForInvoicesAsync(IEnumerable<string> invoiceIds)
    {
        // premium-billing-service has no "list drafts" endpoint, only drafts
        // per invoice, so a run's drafts are read invoice by invoice.
        using var gate = new SemaphoreSlim(4);
        var tasks = invoiceIds.Distinct().Select(async id =>
        {
            await gate.WaitAsync();
            try { return await GetDraftsByInvoiceAsync(id); }
            finally { gate.Release(); }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(d => d).OrderByDescending(d => d.CreatedAt).ToList();
    }

    public Task<EftDraft> InitiateDraftAsync(InitiateEftDraftRequest request)
        => SendAsync<EftDraft>(HttpMethod.Post, "eft/drafts", request);

    public Task<BatchEftResult> InitiateBatchDraftsAsync(InitiateBatchEftRequest request)
        => SendAsync<BatchEftResult>(HttpMethod.Post, "eft/drafts/batch", request);

    public Task<NachaFileResult> GenerateNachaFileAsync()
        => SendAsync<NachaFileResult>(HttpMethod.Post, "eft/nacha/generate");

    public Task<List<NachaHeldFile>> GetHeldNachaFilesAsync()
        => SendAsync<List<NachaHeldFile>>(HttpMethod.Get, "eft/nacha/held");

    public Task<NachaFileResult> RetryNachaTransmissionAsync(string fileReference)
        => SendAsync<NachaFileResult>(HttpMethod.Post, $"eft/nacha/held/{Uri.EscapeDataString(fileReference)}/retry");

    public async Task<MemberPremiumSummary?> GetMemberPremiumSummaryAsync(string memberId)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var response = await _httpClient.GetAsync($"{baseUrl}/v1/members/{Uri.EscapeDataString(memberId)}/premium-summary");
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MemberPremiumSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }
}
