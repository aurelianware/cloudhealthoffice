using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

// ── Payment Run Service ─────────────────────────────────────────────────

public class PaymentRunService : IPaymentRunService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentRunService> _logger;

    public PaymentRunService(HttpClient httpClient, IConfiguration configuration, ILogger<PaymentRunService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<PaymentRunSummary>> GetPaymentRunsAsync(int limit = 50)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<PaymentRunSummary>>($"{baseUrl}/paymentruns?limit={limit}");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<PaymentRunDetails?> GetPaymentRunByIdAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<PaymentRunDetails>($"{baseUrl}/paymentruns/{runId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<string> CreatePaymentRunAsync(CreatePaymentRunRequest request)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/paymentruns", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<CreateRunResponse>();
            return result?.RunId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task CancelPaymentRunAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var response = await _httpClient.PostAsync($"{baseUrl}/paymentruns/{runId}/cancel", null);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<Stream> DownloadEraForRunAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/paymentruns/{runId}/835");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    private class CreateRunResponse { public string RunId { get; set; } = string.Empty; }
}
