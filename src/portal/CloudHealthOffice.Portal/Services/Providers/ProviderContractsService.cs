using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

// ── Provider Contracts Service ──────────────────────────────────────────────

public class ProviderContractsService : IProviderContractsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProviderContractsService> _logger;

    public ProviderContractsService(HttpClient httpClient, IConfiguration configuration, ILogger<ProviderContractsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:ProviderContractsService"] ?? "http://provider-contracts-service.cloudhealthoffice/api";

    public async Task<List<ProviderContractSummary>> GetContractsAsync(
        string? npi = null, string? lob = null,
        string? status = null, string? paymentMethodology = null,
        string? networkStatus = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(npi)) qs.Add($"npi={npi}");
            if (!string.IsNullOrEmpty(lob)) qs.Add($"lob={lob}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            if (!string.IsNullOrEmpty(paymentMethodology)) qs.Add($"paymentMethodology={paymentMethodology}");
            if (!string.IsNullOrEmpty(networkStatus)) qs.Add($"networkStatus={networkStatus}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<ProviderContractSummary>>($"{BaseUrl}/v1/contracts{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task<ProviderContractSummary?> GetContractByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<ProviderContractSummary>($"{BaseUrl}/v1/contracts/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task<ProviderContractSummary?> GetContractByNumberAsync(string number)
    {
        try { return await _httpClient.GetFromJsonAsync<ProviderContractSummary>($"{BaseUrl}/v1/contracts/number/{number}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task<string> CreateContractAsync(ProviderContractSummary contract)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/contracts", contract);
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<ProviderContractSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task UpdateContractAsync(string id, ProviderContractSummary contract)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/contracts/{id}", contract);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task ActivateContractAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/contracts/{id}/activate", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task SuspendContractAsync(string id, string reason)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/contracts/{id}/suspend", new { reason }); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task TerminateContractAsync(string id, string reason, DateTime? terminationDate = null)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/contracts/{id}/terminate", new { reason, terminationDate }); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task ReinstateContractAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/contracts/{id}/reinstate", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task AddAmendmentAsync(string id, ContractAmendmentSummary amendment)
    {
        try { var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/contracts/{id}/amendments", amendment); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task SyncChildrenAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/contracts/{id}/sync-children", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task<List<string>> GetRateConfigIdsAsync(string id)
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<RateConfigIdsResponse>($"{BaseUrl}/v1/contracts/{id}/rate-configs");
            var ids = new List<string>();
            if (result != null)
            {
                ids.AddRange(result.CapitationRateConfigIds ?? new());
                ids.AddRange(result.FfsRateConfigIds ?? new());
            }
            return ids;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    private class RateConfigIdsResponse
    {
        public List<string>? CapitationRateConfigIds { get; set; }
        public List<string>? FfsRateConfigIds { get; set; }
    }
}
