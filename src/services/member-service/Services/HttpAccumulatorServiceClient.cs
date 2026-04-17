using System.Net.Http.Json;
using MemberService.Controllers;
using Microsoft.Extensions.Options;

namespace MemberService.Services;

public sealed class HttpAccumulatorServiceClient : IAccumulatorServiceClient
{
    private readonly HttpClient _http;
    private readonly DownstreamService? _options;
    private const string ServiceName = "accumulator-service";

    public HttpAccumulatorServiceClient(HttpClient http, IOptions<DownstreamOptions> options)
    {
        _http = http;
        _options = options.Value.AccumulatorService;
    }

    public async Task<MemberAccumulatorsResponse> GetAccumulatorsAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options?.BaseUrl))
            throw new DownstreamUnavailableException(
                ServiceName,
                "Configure Downstream:AccumulatorService:BaseUrl to enable accumulator lookup.");

        var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/members/{Uri.EscapeDataString(memberId)}/accumulators");
        req.Headers.Add("X-Tenant-ID", tenantId);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<MemberAccumulatorsResponse>(cancellationToken: ct)
                ?? throw new DownstreamUnavailableException(ServiceName, "Empty response body");
        }
        catch (HttpRequestException ex)
        {
            throw new DownstreamUnavailableException(ServiceName, ex.Message, ex);
        }
    }
}
