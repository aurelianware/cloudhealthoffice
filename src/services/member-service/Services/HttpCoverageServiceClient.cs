using System.Net.Http.Json;
using MemberService.Controllers;
using Microsoft.Extensions.Options;

namespace MemberService.Services;

/// <summary>
/// <see cref="ICoverageServiceClient"/> backed by a configured HttpClient.
/// When <c>Downstream:CoverageService:BaseUrl</c> is not set, every call throws
/// <see cref="DownstreamUnavailableException"/> so the controller returns 503.
/// </summary>
public sealed class HttpCoverageServiceClient : ICoverageServiceClient
{
    private readonly HttpClient _http;
    private readonly DownstreamService? _options;
    private const string ServiceName = "coverage-service";

    public HttpCoverageServiceClient(HttpClient http, IOptions<DownstreamOptions> options)
    {
        _http = http;
        _options = options.Value.CoverageService;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options?.BaseUrl))
            throw new DownstreamUnavailableException(
                ServiceName,
                "Configure Downstream:CoverageService:BaseUrl to enable coverage integrations.");
    }

    public async Task<MemberPcpResponse> GetPcpAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/coverage/member/{Uri.EscapeDataString(memberId)}/pcp");
        req.Headers.Add("X-Tenant-ID", tenantId);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<MemberPcpResponse>(cancellationToken: ct)
                ?? throw new DownstreamUnavailableException(ServiceName, "Empty response body");
        }
        catch (HttpRequestException ex)
        {
            throw new DownstreamUnavailableException(ServiceName, ex.Message, ex);
        }
    }

    public async Task<MemberPcpResponse> AssignPcpAsync(
        string tenantId, string memberId, AssignPcpRequest request, CancellationToken ct = default)
    {
        EnsureConfigured();
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/coverage/member/{Uri.EscapeDataString(memberId)}/pcp")
        {
            Content = JsonContent.Create(request)
        };
        req.Headers.Add("X-Tenant-ID", tenantId);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<MemberPcpResponse>(cancellationToken: ct)
                ?? throw new DownstreamUnavailableException(ServiceName, "Empty response body");
        }
        catch (HttpRequestException ex)
        {
            throw new DownstreamUnavailableException(ServiceName, ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<CoverageHistoryEvent>> GetCoverageHistoryAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/coverage/member/{Uri.EscapeDataString(memberId)}/history");
        req.Headers.Add("X-Tenant-ID", tenantId);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<List<CoverageHistoryEvent>>(cancellationToken: ct)
                ?? new List<CoverageHistoryEvent>();
        }
        catch (HttpRequestException ex)
        {
            throw new DownstreamUnavailableException(ServiceName, ex.Message, ex);
        }
    }

    public async Task TerminateCoverageAsync(
        string tenantId, string memberId, TerminateMemberRequest request, CancellationToken ct = default)
    {
        EnsureConfigured();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/coverage/member/{Uri.EscapeDataString(memberId)}/terminate")
        {
            Content = JsonContent.Create(request)
        };
        req.Headers.Add("X-Tenant-ID", tenantId);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            throw new DownstreamUnavailableException(ServiceName, ex.Message, ex);
        }
    }
}
