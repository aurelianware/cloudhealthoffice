using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

// ── Capitation Service ────────────────────────────────────────────────────

public class CapitationService : ICapitationService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CapitationService> _logger;

    public CapitationService(HttpClient httpClient, IConfiguration configuration, ILogger<CapitationService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:CapitationService"] ?? "http://capitation-service.cloudhealthoffice/api";

    // Contracts
    public async Task<List<CapitationContractSummary>> GetContractsAsync(string? npi = null, string? status = null, string? lob = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(npi)) qs.Add($"npi={npi}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            if (!string.IsNullOrEmpty(lob)) qs.Add($"lob={lob}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<CapitationContractSummary>>($"{BaseUrl}/v1/capitation/contracts{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapitationContractSummary?> GetContractByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<CapitationContractSummary>($"{BaseUrl}/v1/capitation/contracts/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<string> CreateContractAsync(CapitationContractSummary contract)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/capitation/contracts", contract);
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<CapitationContractSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task UpdateContractAsync(string id, CapitationContractSummary contract)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/capitation/contracts/{id}", contract);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task ActivateContractAsync(string id)
    {
        try
        {
            var response = await _httpClient.PutAsync($"{BaseUrl}/v1/capitation/contracts/{id}/activate", null);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task TerminateContractAsync(string id, string reason, DateTime? terminationDate = null)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/capitation/contracts/{id}/terminate",
                new { Reason = reason, TerminationDate = terminationDate });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    // Runs
    public async Task<List<CapRunSummary>> GetRunsAsync(DateTime? from = null, DateTime? to = null, string? lineOfBusiness = null)
    {
        try
        {
            var qs = new List<string>();
            if (from.HasValue) qs.Add($"from={from.Value:O}");
            if (to.HasValue) qs.Add($"to={to.Value:O}");
            if (!string.IsNullOrEmpty(lineOfBusiness)) qs.Add($"lineOfBusiness={lineOfBusiness}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<CapRunSummary>>($"{BaseUrl}/v1/capitation/runs{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapRunSummary?> GetRunByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<CapRunSummary>($"{BaseUrl}/v1/capitation/runs/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<string> CreateRunAsync(CreateCapRunRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/capitation/runs", request);
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<CapRunSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapRunSummary> ExecuteRunAsync(string id)
    {
        try
        {
            var response = await _httpClient.PostAsync($"{BaseUrl}/v1/capitation/runs/{id}/execute", null);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<CapRunSummary>() ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task CancelRunAsync(string id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"{BaseUrl}/v1/capitation/runs/{id}");
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    // Statements
    public async Task<List<CapStatementSummary>> GetStatementsAsync(string? npi = null, DateTime? periodFrom = null, DateTime? periodTo = null, string? status = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(npi)) qs.Add($"npi={npi}");
            if (periodFrom.HasValue) qs.Add($"periodFrom={periodFrom.Value:O}");
            if (periodTo.HasValue) qs.Add($"periodTo={periodTo.Value:O}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<CapStatementSummary>>($"{BaseUrl}/v1/capitation/statements{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapStatementSummary?> GetStatementByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<CapStatementSummary>($"{BaseUrl}/v1/capitation/statements/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<List<CapStatementSummary>> GetStatementsByRunAsync(string runId)
    {
        try { return await _httpClient.GetFromJsonAsync<List<CapStatementSummary>>($"{BaseUrl}/v1/capitation/runs/{runId}/statements") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<List<CapStatementSummary>> GetUnpaidStatementsAsync()
    {
        try { return await _httpClient.GetFromJsonAsync<List<CapStatementSummary>>($"{BaseUrl}/v1/capitation/statements/unpaid") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task ApproveStatementAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/capitation/statements/{id}/approve", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task VoidStatementAsync(string id, string reason)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/capitation/statements/{id}/void", new { Reason = reason }); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task HoldStatementAsync(string id, string reason)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/capitation/statements/{id}/hold", new { Reason = reason }); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapitationPeriodSummaryDto> GetPeriodSummaryAsync(DateTime period)
    {
        try { return await _httpClient.GetFromJsonAsync<CapitationPeriodSummaryDto>($"{BaseUrl}/v1/capitation/statements/summary?period={period:O}") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    // Disbursements
    public async Task<string> InitiateDisbursementAsync(string statementId, string? initiatedBy = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/capitation/disbursements",
                new { StatementId = statementId, InitiatedBy = initiatedBy });
            response.EnsureSuccessStatusCode();
            return "ok";
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapDisbursementBatchResult> InitiateBatchDisbursementAsync(List<string> statementIds, string? initiatedBy = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/capitation/disbursements/batch",
                new { StatementIds = statementIds, InitiatedBy = initiatedBy });
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<CapDisbursementBatchResult>() ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<List<NachaHeldFile>> GetHeldNachaFilesAsync()
    {
        try { return await _httpClient.GetFromJsonAsync<List<NachaHeldFile>>($"{BaseUrl}/v1/capitation/disbursements/nacha/held") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<NachaFileResult> RetryNachaTransmissionAsync(string fileReference)
    {
        try
        {
            var response = await _httpClient.PostAsync(
                $"{BaseUrl}/v1/capitation/disbursements/nacha/held/{Uri.EscapeDataString(fileReference)}/retry", null);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Retry refused ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
            return await response.Content.ReadFromJsonAsync<NachaFileResult>() ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }
}
