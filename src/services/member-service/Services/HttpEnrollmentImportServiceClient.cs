using System.Net.Http.Json;
using MemberService.Controllers;
using Microsoft.Extensions.Options;

namespace MemberService.Services;

public sealed class HttpEnrollmentImportServiceClient : IEnrollmentImportServiceClient
{
    private readonly HttpClient _http;
    private readonly DownstreamService? _options;
    private const string ServiceName = "enrollment-import-service";

    public HttpEnrollmentImportServiceClient(HttpClient http, IOptions<DownstreamOptions> options)
    {
        _http = http;
        _options = options.Value.EnrollmentImportService;
    }

    public async Task<IReadOnlyList<Enrollment834Record>> Get834TransactionsAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options?.BaseUrl))
            throw new DownstreamUnavailableException(
                ServiceName,
                "Configure Downstream:EnrollmentImportService:BaseUrl to enable 834 history.");

        var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/enrollment/transactions?memberId={Uri.EscapeDataString(memberId)}");
        req.Headers.Add("X-Tenant-ID", tenantId);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<List<Enrollment834Record>>(cancellationToken: ct)
                ?? new List<Enrollment834Record>();
        }
        catch (HttpRequestException ex)
        {
            throw new DownstreamUnavailableException(ServiceName, ex.Message, ex);
        }
    }

    public async Task<EnrollmentEventListResponse> GetEnrollmentEventsAsync(
        string tenantId,
        string memberId,
        string? type = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 50,
        string? continuationToken = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options?.BaseUrl))
            throw new DownstreamUnavailableException(
                ServiceName,
                "Configure Downstream:EnrollmentImportService:BaseUrl to enable enrollment events.");

        var qs = new List<string> { $"limit={Math.Clamp(limit, 1, 200)}" };
        if (!string.IsNullOrWhiteSpace(type)) qs.Add($"type={Uri.EscapeDataString(type)}");
        if (from.HasValue) qs.Add($"from={Uri.EscapeDataString(from.Value.ToString("o"))}");
        if (to.HasValue) qs.Add($"to={Uri.EscapeDataString(to.Value.ToString("o"))}");
        if (!string.IsNullOrWhiteSpace(continuationToken))
            qs.Add($"continuationToken={Uri.EscapeDataString(continuationToken)}");

        var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/members/{Uri.EscapeDataString(memberId)}/enrollment-events?{string.Join("&", qs)}");
        req.Headers.Add("X-Tenant-ID", tenantId);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<EnrollmentEventListResponse>(cancellationToken: ct)
                ?? new EnrollmentEventListResponse();
        }
        catch (HttpRequestException ex)
        {
            throw new DownstreamUnavailableException(ServiceName, ex.Message, ex);
        }
    }
}
