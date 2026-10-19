using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

public class MemberService : IMemberService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MemberService> _logger;

    public MemberService(HttpClient httpClient, IConfiguration configuration, ILogger<MemberService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<MemberSummary>> SearchMembersAsync(string searchTerm)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var members = await _httpClient.GetFromJsonAsync<List<MemberSummary>>($"{baseUrl}/members/search?q={Uri.EscapeDataString(searchTerm)}");
            return members ?? new List<MemberSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberDetails?> GetMemberByIdAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<MemberDetails>($"{baseUrl}/members/{memberId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberPcp?> GetMemberPcpAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<MemberPcp>($"{baseUrl}/members/{memberId}/pcp");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<PcpAssignmentOutcome> AssignPcpAsync(AssignPcpRequest request)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/members/{request.MemberId}/pcp", request);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                PcpValidationProblem? problem = null;
                try
                {
                    problem = await response.Content.ReadFromJsonAsync<PcpValidationProblem>();
                }
                catch (JsonException) { /* malformed body → fall through to default */ }

                return new PcpAssignmentOutcome
                {
                    ValidationError = problem ?? new PcpValidationProblem
                    {
                        Code = "VALIDATION_FAILED",
                        Message = "PCP assignment was rejected."
                    }
                };
            }
            response.EnsureSuccessStatusCode();
            var pcp = await response.Content.ReadFromJsonAsync<MemberPcp>();
            if (pcp is null)
            {
                // 2xx with empty/invalid body is a contract break — surface as
                // unavailable so the UI gets a deterministic failure path
                // rather than a warning with an empty message.
                throw new ServiceUnavailableException("Member Service",
                    new HttpRequestException("Member Service returned an empty PCP assignment response."));
            }
            return new PcpAssignmentOutcome { Pcp = pcp };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<List<PcpAssignmentHistoryItem>> GetMemberPcpHistoryAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var history = await _httpClient.GetFromJsonAsync<List<PcpAssignmentHistoryItem>>(
                $"{baseUrl}/members/{memberId}/pcp/history");
            return history ?? new List<PcpAssignmentHistoryItem>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<List<CoverageHistoryEvent>> GetCoverageHistoryAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var history = await _httpClient.GetFromJsonAsync<List<CoverageHistoryEvent>>($"{baseUrl}/members/{memberId}/coverage-history");
            return history ?? new List<CoverageHistoryEvent>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<List<Enrollment834Record>> GetMember834TransactionsAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var records = await _httpClient.GetFromJsonAsync<List<Enrollment834Record>>($"{baseUrl}/members/{memberId}/834-transactions");
            return records ?? new List<Enrollment834Record>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<EnrollmentEventPage> GetEnrollmentEventsAsync(string memberId, EnrollmentEventFilter filter)
    {
        var baseUrl = _configuration["Services:MemberService"];
        var qs = new List<string> { $"limit={Math.Clamp(filter.Limit, 1, 200)}" };
        if (!string.IsNullOrWhiteSpace(filter.Type)) qs.Add($"type={Uri.EscapeDataString(filter.Type)}");
        if (filter.From.HasValue) qs.Add($"from={Uri.EscapeDataString(filter.From.Value.ToString("o"))}");
        if (filter.To.HasValue) qs.Add($"to={Uri.EscapeDataString(filter.To.Value.ToString("o"))}");
        if (!string.IsNullOrWhiteSpace(filter.ContinuationToken))
            qs.Add($"continuationToken={Uri.EscapeDataString(filter.ContinuationToken)}");

        try
        {
            var url = $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/enrollment-events?{string.Join("&", qs)}";
            var page = await _httpClient.GetFromJsonAsync<EnrollmentEventPage>(url);
            return page ?? new EnrollmentEventPage();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task TerminateEnrollmentAsync(TerminateEnrollmentRequest request)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/members/{request.MemberId}/terminate", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberAccumulators> GetAccumulatorsAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var accums = await _httpClient.GetFromJsonAsync<MemberAccumulators>($"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/accumulators");
            return accums ?? new MemberAccumulators();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }
}
