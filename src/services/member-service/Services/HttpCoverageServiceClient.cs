using System.Net.Http.Json;
using System.Text.Json;
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

    public async Task<AssignPcpOutcome> AssignPcpAsync(
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
            if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                // Coverage-service emits a structured PcpValidationError on 400.
                // Surface it through AssignPcpOutcome so the controller can
                // forward it as 400 — do NOT throw, this is a normal validation
                // outcome, not a downstream failure. If the body is missing or
                // non-JSON (unexpected) we still degrade to a generic error
                // rather than raising a 500.
                PcpValidationProblem? problem = null;
                try
                {
                    problem = await resp.Content.ReadFromJsonAsync<PcpValidationProblem>(cancellationToken: ct);
                }
                catch (JsonException) { /* fall through to default */ }
                catch (NotSupportedException) { /* non-JSON content-type */ }

                return new AssignPcpOutcome
                {
                    ValidationError = problem ?? new PcpValidationProblem
                    {
                        Code = "VALIDATION_FAILED",
                        Message = "PCP assignment rejected by coverage-service."
                    }
                };
            }
            resp.EnsureSuccessStatusCode();
            var pcp = await resp.Content.ReadFromJsonAsync<MemberPcpResponse>(cancellationToken: ct)
                ?? throw new DownstreamUnavailableException(ServiceName, "Empty response body");
            return new AssignPcpOutcome { Pcp = pcp };
        }
        catch (HttpRequestException ex)
        {
            throw new DownstreamUnavailableException(ServiceName, ex.Message, ex);
        }
        catch (JsonException ex)
        {
            // Success body that didn't match the expected shape → treat as 503.
            throw new DownstreamUnavailableException(ServiceName, ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<PcpAssignmentHistoryItem>> GetPcpHistoryAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/coverage/member/{Uri.EscapeDataString(memberId)}/pcp/history");
        req.Headers.Add("X-Tenant-ID", tenantId);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<List<PcpAssignmentHistoryItem>>(cancellationToken: ct)
                ?? new List<PcpAssignmentHistoryItem>();
        }
        catch (HttpRequestException ex)
        {
            throw new DownstreamUnavailableException(ServiceName, ex.Message, ex);
        }
        catch (JsonException ex)
        {
            // Coverage-service wire shape drift — better to 503 than to leak a 500.
            throw new DownstreamUnavailableException(
                ServiceName, $"PCP history response deserialization failed: {ex.Message}", ex);
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
