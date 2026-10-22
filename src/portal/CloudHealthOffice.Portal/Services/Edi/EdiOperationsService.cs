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

}
