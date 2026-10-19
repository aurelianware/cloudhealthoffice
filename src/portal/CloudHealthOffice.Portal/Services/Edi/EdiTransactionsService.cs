using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Driver;

namespace CloudHealthOffice.Portal.Services;

public class EdiTransactionsService : IEdiTransactionsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EdiTransactionsService> _logger;

    public EdiTransactionsService(HttpClient httpClient, IConfiguration configuration, ILogger<EdiTransactionsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<Enrollment834Record>> GetEnrollment834TransactionsAsync(int limit = 100)
    {
        var baseUrl = _configuration["Services:EnrollmentImportService"];
        try
        {
            var records = await _httpClient.GetFromJsonAsync<List<Enrollment834Record>>(
                $"{baseUrl}/v1/enrollment/transactions/recent?limit={Math.Clamp(limit, 1, 500)}", JsonOptions);
            return records ?? new List<Enrollment834Record>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Enrollment Import Service");
            throw new ServiceUnavailableException("Enrollment Import Service", ex);
        }
    }

    public async Task<List<ClaimImportTransactionRecord>> GetClaimImportTransactionsAsync(int limit = 100)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var records = await _httpClient.GetFromJsonAsync<List<ClaimImportTransactionRecord>>(
                $"{baseUrl}/v1/claims/import-transactions?limit={Math.Clamp(limit, 1, 500)}", JsonOptions);
            return records ?? new List<ClaimImportTransactionRecord>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<EnrollmentImportRunRecord>> GetEnrollmentImportRunsAsync(int limit = 100)
    {
        var baseUrl = _configuration["Services:EnrollmentImportService"];
        try
        {
            var records = await _httpClient.GetFromJsonAsync<List<EnrollmentImportRunRecord>>(
                $"{baseUrl}/v1/enrollment/import-runs?limit={Math.Clamp(limit, 1, 500)}", JsonOptions);
            return records ?? new List<EnrollmentImportRunRecord>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Enrollment Import Service");
            throw new ServiceUnavailableException("Enrollment Import Service", ex);
        }
    }
}
