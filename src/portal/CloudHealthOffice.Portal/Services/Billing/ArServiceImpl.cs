using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

// ── AR Service ──────────────────────────────────────────────────────────────

public class ArServiceImpl : IArService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ArServiceImpl> _logger;

    public ArServiceImpl(HttpClient httpClient, IConfiguration configuration, ILogger<ArServiceImpl> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:ArService"] ?? "http://ar-service.cloudhealthoffice/api";

    // ── GL Accounts ─────────────────────────────────────────────────────
    public async Task<List<GlAccountSummary>> GetAccountsAsync(string? accountType = null, string? lob = null, string? status = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(accountType)) qs.Add($"accountType={accountType}");
            if (!string.IsNullOrEmpty(lob)) qs.Add($"lob={lob}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<GlAccountSummary>>($"{BaseUrl}/v1/ar/accounts{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<GlAccountSummary?> GetAccountByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<GlAccountSummary>($"{BaseUrl}/v1/ar/accounts/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<string> CreateAccountAsync(GlAccountSummary account)
    {
        try
        {
            var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/ar/accounts", account);
            r.EnsureSuccessStatusCode();
            var created = await r.Content.ReadFromJsonAsync<GlAccountSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task UpdateAccountAsync(string id, GlAccountSummary account)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/ar/accounts/{id}", account); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task ActivateAccountAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/ar/accounts/{id}/activate", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task DeactivateAccountAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/ar/accounts/{id}/deactivate", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    // ── Balances ────────────────────────────────────────────────────────
    public async Task<List<ArBalanceSummary>> GetBalancesAsync(string? accountId = null, DateTime? period = null, bool? isReconciled = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(accountId)) qs.Add($"accountId={accountId}");
            if (period.HasValue) qs.Add($"period={period.Value:yyyy-MM-dd}");
            if (isReconciled.HasValue) qs.Add($"isReconciled={isReconciled.Value}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<ArBalanceSummary>>($"{BaseUrl}/v1/ar/balances{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<ArBalanceSummary?> GetBalanceByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<ArBalanceSummary>($"{BaseUrl}/v1/ar/balances/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<List<ArBalanceSummary>> GetBalancesByAccountAsync(string accountId)
    {
        try { return await _httpClient.GetFromJsonAsync<List<ArBalanceSummary>>($"{BaseUrl}/v1/ar/balances/account/{accountId}") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<ArAgingSummary> GetAgingSummaryAsync()
    {
        try { return await _httpClient.GetFromJsonAsync<ArAgingSummary>($"{BaseUrl}/v1/ar/balances/aging") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task ReconcileBalanceAsync(string id)
    {
        try { var r = await _httpClient.PostAsync($"{BaseUrl}/v1/ar/balances/{id}/reconcile", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    // ── Cash Posting ────────────────────────────────────────────────────
    public async Task<List<CashPostingSummary>> GetCashPostingsAsync(string? payerType = null, string? status = null, DateTime? dateFrom = null, DateTime? dateTo = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(payerType)) qs.Add($"payerType={payerType}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            if (dateFrom.HasValue) qs.Add($"dateFrom={dateFrom.Value:yyyy-MM-dd}");
            if (dateTo.HasValue) qs.Add($"dateTo={dateTo.Value:yyyy-MM-dd}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<CashPostingSummary>>($"{BaseUrl}/v1/ar/cash-postings{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<CashPostingSummary?> GetCashPostingByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<CashPostingSummary>($"{BaseUrl}/v1/ar/cash-postings/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<string> CreateCashPostingAsync(CashPostingSummary posting)
    {
        try
        {
            var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/ar/cash-postings", posting);
            r.EnsureSuccessStatusCode();
            var created = await r.Content.ReadFromJsonAsync<CashPostingSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task ApplyCashPostingAsync(string id)
    {
        try
        {
            var r = await _httpClient.PostAsync($"{BaseUrl}/v1/ar/cash-postings/{id}/apply", null);
            await ThrowIfRequiresReconciliationAsync(r, id, "applied");
            r.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task VoidCashPostingAsync(string id)
    {
        try
        {
            var r = await _httpClient.PostAsync($"{BaseUrl}/v1/ar/cash-postings/{id}/void", null);
            await ThrowIfRequiresReconciliationAsync(r, id, "voided");
            r.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    /// <summary>A 409 LegacyPostingRequiresReconciliation is a business refusal, not an outage.</summary>
    private async Task ThrowIfRequiresReconciliationAsync(HttpResponseMessage response, string id, string action)
    {
        if (response.StatusCode != HttpStatusCode.Conflict)
            return;
        string? code = null;
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                code = value.GetString();
        }
        catch (JsonException) { }
        if (code == CashPostingRequiresReconciliationException.Code)
        {
            _logger.LogWarning("Cash posting {PostingId} cannot be {Action}: legacy posting awaiting finance reconciliation", id, action);
            throw new CashPostingRequiresReconciliationException(id, action);
        }
    }

    // ── Adjustments ─────────────────────────────────────────────────────
    public async Task<List<ArAdjustmentSummary>> GetAdjustmentsAsync(string? type = null, string? status = null, DateTime? period = null, string? accountId = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(type)) qs.Add($"type={type}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            if (period.HasValue) qs.Add($"period={period.Value:yyyy-MM-dd}");
            if (!string.IsNullOrEmpty(accountId)) qs.Add($"glAccountId={accountId}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<ArAdjustmentSummary>>($"{BaseUrl}/v1/ar/adjustments{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<ArAdjustmentSummary?> GetAdjustmentByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<ArAdjustmentSummary>($"{BaseUrl}/v1/ar/adjustments/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<string> CreateAdjustmentAsync(ArAdjustmentSummary adjustment)
    {
        try
        {
            var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/ar/adjustments", adjustment);
            r.EnsureSuccessStatusCode();
            var created = await r.Content.ReadFromJsonAsync<ArAdjustmentSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task ApproveAdjustmentAsync(string id)
    {
        try { var r = await _httpClient.PostAsync($"{BaseUrl}/v1/ar/adjustments/{id}/approve", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task RejectAdjustmentAsync(string id, string reason)
    {
        try { var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/ar/adjustments/{id}/reject", new { reason }); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task PostAdjustmentAsync(string id)
    {
        try { var r = await _httpClient.PostAsync($"{BaseUrl}/v1/ar/adjustments/{id}/post", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task ReverseAdjustmentAsync(string id)
    {
        try { var r = await _httpClient.PostAsync($"{BaseUrl}/v1/ar/adjustments/{id}/reverse", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    // ── Batch Rules ─────────────────────────────────────────────────────
    public async Task<List<ArBatchRuleSummary>> GetBatchRulesAsync(string? trigger = null, string? status = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(trigger)) qs.Add($"trigger={trigger}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<ArBatchRuleSummary>>($"{BaseUrl}/v1/ar/batch-rules{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<ArBatchRuleSummary?> GetBatchRuleByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<ArBatchRuleSummary>($"{BaseUrl}/v1/ar/batch-rules/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<string> CreateBatchRuleAsync(ArBatchRuleSummary rule)
    {
        try
        {
            var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/ar/batch-rules", rule);
            r.EnsureSuccessStatusCode();
            var created = await r.Content.ReadFromJsonAsync<ArBatchRuleSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task UpdateBatchRuleAsync(string id, ArBatchRuleSummary rule)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/ar/batch-rules/{id}", rule); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<ArBatchRuleTestResult> TestBatchRuleAsync(string id, decimal sampleAmount)
    {
        try
        {
            var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/ar/batch-rules/{id}/test", new { sampleAmount });
            r.EnsureSuccessStatusCode();
            return await r.Content.ReadFromJsonAsync<ArBatchRuleTestResult>() ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<MemberArSummary?> GetMemberArSummaryAsync(string memberId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{BaseUrl}/v1/members/{Uri.EscapeDataString(memberId)}/ar-summary");
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MemberArSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "AR Service unavailable");
            throw new ServiceUnavailableException("AR Service", ex);
        }
    }
}
