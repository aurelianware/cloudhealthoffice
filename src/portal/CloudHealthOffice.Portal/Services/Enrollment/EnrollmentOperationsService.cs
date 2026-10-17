using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

// ---------------------------------------------------------------------------
// Enrollment Operations Service
// ---------------------------------------------------------------------------

public class EnrollmentOperationsService : IEnrollmentOperationsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EnrollmentOperationsService> _logger;

    public EnrollmentOperationsService(HttpClient httpClient, IConfiguration configuration, ILogger<EnrollmentOperationsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<EnrollmentDailySummary> GetTodaySummaryAsync()
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<EnrollmentDailySummary>($"{baseUrl}/enrollment-ops/summary/today");
            return summary ?? new EnrollmentDailySummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<List<EnrollmentFile>> GetRecentFilesAsync(int days = 7)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var files = await _httpClient.GetFromJsonAsync<List<EnrollmentFile>>($"{baseUrl}/enrollment-ops/files?days={days}");
            return files ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<EnrollmentFileDetail> GetFileDetailAsync(string fileId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var detail = await _httpClient.GetFromJsonAsync<EnrollmentFileDetail>($"{baseUrl}/enrollment-ops/files/{Uri.EscapeDataString(fileId)}");
            return detail ?? throw new Exception($"File {fileId} not found");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }
}
