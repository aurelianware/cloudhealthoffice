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
}
