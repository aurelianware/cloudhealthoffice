using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

// ── EDI Operations Service ─────────────────────────────────────────────

public class EdiOperationsService : IEdiOperationsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EdiOperationsService> _logger;

    public EdiOperationsService(HttpClient httpClient, IConfiguration configuration, ILogger<EdiOperationsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<Edi834Batch>> Get834BatchesAsync(DateTime? from = null, DateTime? to = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var url = $"{baseUrl}/edi/834-batches" +
                (from.HasValue ? $"?from={from:yyyy-MM-dd}" : "") +
                (to.HasValue ? (from.HasValue ? $"&to={to:yyyy-MM-dd}" : $"?to={to:yyyy-MM-dd}") : "");
            var result = await _httpClient.GetFromJsonAsync<List<Edi834Batch>>(url);
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task<List<Enrollment834Record>> Get834BatchRecordsAsync(string batchId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<Enrollment834Record>>($"{baseUrl}/edi/834-batches/{batchId}/records");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task Resolve834RecordAsync(Edi834ResolutionRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/edi/834-batches/{request.BatchId}/resolve", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task<List<ClaimAcknowledgmentSummary>> Get277CaAcknowledgmentsAsync(DateTime? from = null, DateTime? to = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<ClaimAcknowledgmentSummary>>($"{baseUrl}/edi/277ca");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task<Stream> Download277CaAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/claims/{claimId}/277ca");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task<List<EraSummary>> GetErasAsync(DateTime? from = null, DateTime? to = null)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<EraSummary>>($"{baseUrl}/payments/eras");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<Stream> DownloadEraAsync(string paymentId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/payments/{paymentId}/835");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<List<EdiTransactionHistoryItem>> GetTransactionHistoryAsync(DateTime? from, DateTime? to, string? transactionType, string? partnerId, string? status, int pageNumber, int pageSize)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var url = $"{baseUrl}/edi/history?page={pageNumber}&pageSize={pageSize}" +
                (transactionType != null ? $"&type={transactionType}" : "") +
                (status != null ? $"&status={status}" : "");
            var result = await _httpClient.GetFromJsonAsync<List<EdiTransactionHistoryItem>>(url);
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }
}
