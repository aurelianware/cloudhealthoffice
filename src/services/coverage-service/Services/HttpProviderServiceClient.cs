using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace CoverageService.Services;

public sealed class ProviderServiceOptions
{
    public string? BaseUrl { get; set; }
}

/// <summary>
/// HTTP-backed <see cref="IProviderServiceClient"/>. When BaseUrl is unset
/// (typical for local dev / tests) <see cref="GetByNpiAsync"/> returns null —
/// validation will then fail with PROVIDER_NOT_FOUND, which is the right
/// failure mode in a half-configured environment.
/// </summary>
public sealed class HttpProviderServiceClient : IProviderServiceClient
{
    private readonly HttpClient _http;
    private readonly ProviderServiceOptions _options;

    public HttpProviderServiceClient(HttpClient http, IOptions<ProviderServiceOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<ProviderDto?> GetByNpiAsync(string npi, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl)) return null;

        try
        {
            var resp = await _http.GetAsync($"/api/Providers/npi/{Uri.EscapeDataString(npi)}", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<ProviderDto>(cancellationToken: ct);
        }
        catch (HttpRequestException)
        {
            // Network blip / provider-service down — treated as "not found" so
            // assignment fails with PROVIDER_NOT_FOUND rather than 503-ing the
            // member-service caller. Operators see this in provider-service health.
            return null;
        }
    }
}

/// <summary>
/// HTTP-backed <see cref="IPanelCounter"/>. Reads the capitation-service-style
/// roster endpoint already exposed by coverage-service itself
/// (GET /api/v1/coverage/by-pcp/{npi}) — but only counts active rows, which is
/// the same population panel limits gate against.
///
/// Enabled/disabled via <see cref="HttpClient.BaseAddress"/>: when the endpoint
/// URL is not configured in Program.cs we leave BaseAddress null and this
/// counter short-circuits to 0. That is deliberately decoupled from
/// <see cref="ProviderServiceOptions"/>, which belongs to
/// <see cref="HttpProviderServiceClient"/> only — the counter hits a different
/// service (capitation / coverage) and must not inherit provider-service config.
/// </summary>
public sealed class HttpPanelCounter : IPanelCounter
{
    private readonly HttpClient _http;

    public HttpPanelCounter(HttpClient http)
    {
        _http = http;
    }

    public async Task<int> CurrentPanelCountAsync(string tenantId, string providerNpi, CancellationToken ct = default)
    {
        if (_http.BaseAddress is null) return 0;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/coverage/by-pcp/{Uri.EscapeDataString(providerNpi)}?status=1");
            req.Headers.Add("X-Tenant-ID", tenantId);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return 0;
            var rows = await resp.Content.ReadFromJsonAsync<List<Models.Coverage>>(cancellationToken: ct);
            return rows?.Count ?? 0;
        }
        catch (HttpRequestException)
        {
            return 0;
        }
    }
}
