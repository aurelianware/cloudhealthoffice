using System.Net.Http.Json;
using System.Text.Json;
using FhirService.Mappers;
using FhirService.Models;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Provider Directory API controller — exposes FHIR R4 provider directory resources
/// (Practitioner, PractitionerRole, Organization, Location).
///
/// Practitioner, PractitionerRole, and Organization are proxied to provider-service,
/// which owns the canonical CHO projection for each resource type:
///  - Practitioner:      capability 5.7 — proxied to provider-service /fhir/Practitioner
///  - PractitionerRole:  capability 5.8 — proxied to provider-service /fhir/PractitionerRole
///  - Organization:      capability 5.9 — proxied to provider-service /fhir/Organization
///
/// Location is still served from NPPES. The NPPES helpers below are retained for
/// the Location path only; MapNppesToOrganization is deprecated and will be removed
/// in a subsequent cleanup PR.
///
/// Port of the TypeScript provider-directory-api.ts (NPPES path).
/// </summary>
[Route("fhir/r4")]
public class ProviderDirectoryController : FhirControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _verificationClient;
    private readonly HttpClient _providerServiceClient;
    private readonly ILogger<ProviderDirectoryController> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ProviderDirectoryController(
        IHttpClientFactory httpClientFactory,
        ILogger<ProviderDirectoryController> logger)
    {
        _httpClient = httpClientFactory.CreateClient("NppesApi");
        _verificationClient = httpClientFactory.CreateClient("ProviderVerificationService");
        _providerServiceClient = httpClientFactory.CreateClient("ProviderService");
        _logger = logger;
    }

    // ── Practitioner (proxied to provider-service, capability 5.7) ───────────

    /// <summary>
    /// GET /fhir/r4/Practitioner/{id} — read Practitioner by NPI.
    /// Proxies to provider-service /fhir/Practitioner/{id}; fhir-service is
    /// the FHIR façade and provider-service owns the projection
    /// (capability 5.7).
    /// </summary>
    [HttpGet("Practitioner/{id}")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> ReadPractitioner(string id, CancellationToken ct)
        => ProxyProviderServiceAsync("Practitioner", $"fhir/Practitioner/{Uri.EscapeDataString(id)}", ct);

    /// <summary>
    /// GET /fhir/r4/Practitioner?npi=&amp;given=&amp;family=&amp;... — search Practitioners.
    /// Forwards the FHIR search query string to provider-service
    /// /fhir/Practitioner unchanged (capability 5.7).
    /// </summary>
    [HttpGet("Practitioner")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> SearchPractitioners(CancellationToken ct = default)
    {
        var qs = HttpContext.Request.QueryString.HasValue
            ? HttpContext.Request.QueryString.Value
            : string.Empty;
        return ProxyProviderServiceAsync("Practitioner", $"fhir/Practitioner{qs}", ct);
    }

    /// <summary>
    /// Generic proxy hop to provider-service for the FHIR resources
    /// where provider-service is the canonical authority (capability 5.7
    /// — Practitioner; capability 5.8 — PractitionerRole). Status pass
    /// through, 5xx → 502 OperationOutcome, transport faults → 502.
    /// Resource label flows into structured-log fields so operators can
    /// distinguish Practitioner vs PractitionerRole proxy failures.
    /// </summary>
    private async Task<IActionResult> ProxyProviderServiceAsync(
        string resourceLabel,
        string path,
        CancellationToken ct)
    {
        // `path` is derived from the user-supplied URL / query string and
        // flows into structured-log fields below. Sanitize once up front
        // so all log sites share the same scrubbed value (CodeQL: log
        // entries created from user input).
        var loggablePath = SanitizeForLog(path);
        try
        {
            using var upstream = await _providerServiceClient.GetAsync(path, ct);
            var body = await upstream.Content.ReadAsStringAsync(ct);
            var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/fhir+json";

            // Pass status + body through verbatim. provider-service emits
            // FHIR OperationOutcome on 4xx, so the proxy needs to forward
            // those without rewrapping. 5xx responses are mapped to a
            // FHIR 502 OperationOutcome — exposing upstream 5xx bodies
            // could leak internal detail.
            if ((int)upstream.StatusCode >= 500)
            {
                _logger.LogWarning(
                    "provider-service {Resource} upstream returned {Status} for {Path}",
                    resourceLabel, (int)upstream.StatusCode, loggablePath);
                return FhirBadGateway($"{resourceLabel} upstream is unavailable.");
            }

            return new ContentResult
            {
                Content = body,
                ContentType = contentType,
                StatusCode = (int)upstream.StatusCode
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "provider-service {Resource} proxy hop failed for {Path}",
                resourceLabel, loggablePath);
            return FhirBadGateway($"{resourceLabel} upstream is unreachable.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled (client disconnect, server abort). Don't
            // pretend the upstream timed out — propagate cancellation so
            // the request pipeline returns its standard 499/aborted shape
            // and we don't pollute logs / metrics with phantom 502s.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient surfaces its own configured timeout as
            // TaskCanceledException; ct was NOT cancelled (handled above).
            // That genuinely is an upstream-too-slow → 502.
            _logger.LogWarning(ex,
                "provider-service {Resource} proxy hop timed out for {Path}",
                resourceLabel, loggablePath);
            return FhirBadGateway($"{resourceLabel} upstream timed out.");
        }
    }

    // ── Organization (proxied to provider-service, capability 5.9) ──────────

    /// <summary>
    /// GET /fhir/r4/Organization/{id} — read Organization by id.
    /// Proxies to provider-service /fhir/Organization/{id}; provider-service
    /// owns the canonical CHO projection (capability 5.9). The id is
    /// shape-detected by provider-service: 10-digit NPI → Provider-as-Org
    /// (type=prov); anything else → Organization network entity (type=ins).
    /// </summary>
    [HttpGet("Organization/{id}")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> ReadOrganization(string id, CancellationToken ct)
        => ProxyProviderServiceAsync("Organization", $"fhir/Organization/{Uri.EscapeDataString(id)}", ct);

    /// <summary>
    /// GET /fhir/r4/Organization?npi=&amp;name=&amp;... — search Organizations.
    /// Forwards the FHIR search query string to provider-service
    /// /fhir/Organization unchanged (capability 5.9). Preserves the existing
    /// npi / name / city / state / postal-code parameter surface; adds
    /// identifier=ORG:{orgId} for network-entity chain-key lookup and
    /// type=prov|ins for source-entity discrimination.
    /// </summary>
    [HttpGet("Organization")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> SearchOrganizations(CancellationToken ct = default)
    {
        var qs = HttpContext.Request.QueryString.HasValue
            ? HttpContext.Request.QueryString.Value
            : string.Empty;
        return ProxyProviderServiceAsync("Organization", $"fhir/Organization{qs}", ct);
    }

    // ── PractitionerRole (proxied to provider-service, capability 5.8) ───────

    /// <summary>
    /// GET /fhir/r4/PractitionerRole/{id} — read PractitionerRole by composite id.
    /// Proxies to provider-service /fhir/PractitionerRole/{id}; provider-service
    /// owns the projection (capability 5.8). The id format is the
    /// composite-tuple shape <c>{npi}-{lobInt}-{yyyymmdd}-{networkId}</c>
    /// per the projection's <c>EncodeId</c>.
    /// </summary>
    [HttpGet("PractitionerRole/{id}")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> ReadPractitionerRole(string id, CancellationToken ct)
        => ProxyProviderServiceAsync(
            "PractitionerRole",
            $"fhir/PractitionerRole/{Uri.EscapeDataString(id)}",
            ct);

    /// <summary>
    /// GET /fhir/r4/PractitionerRole?practitioner=&amp;organization=&amp;specialty=&amp;... — search PractitionerRoles.
    /// Forwards the FHIR search query string to provider-service
    /// /fhir/PractitionerRole unchanged (capability 5.8).
    /// </summary>
    [HttpGet("PractitionerRole")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> SearchPractitionerRoles(CancellationToken ct = default)
    {
        var qs = HttpContext.Request.QueryString.HasValue
            ? HttpContext.Request.QueryString.Value
            : string.Empty;
        return ProxyProviderServiceAsync("PractitionerRole", $"fhir/PractitionerRole{qs}", ct);
    }

    // ── Location ─────────────────────────────────────────────────────────────

    /// <summary>GET /fhir/r4/Location/{id} — read Location by ID ({NPI}-loc-{index})</summary>
    [HttpGet("Location/{id}")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> ReadLocation(string id, CancellationToken ct)
    {
        var match = System.Text.RegularExpressions.Regex.Match(id, @"^(\d{10})-loc-(\d+)$");
        if (!match.Success)
            return FhirNotFound("Location", id);

        var npi = match.Groups[1].Value;
        var index = int.Parse(match.Groups[2].Value);

        var nppes = await LookupNppesAsync(npi, ct);
        if (nppes is null || index >= nppes.Addresses.Count)
            return FhirNotFound("Location", id);

        var location = ProviderDirectoryMapper.MapNppesToLocation(nppes, index);
        return Ok(location);
    }

    /// <summary>GET /fhir/r4/Location?organization=&amp;city=&amp;... — search Locations</summary>
    [HttpGet("Location")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> SearchLocations(
        [FromQuery] string? organization,
        [FromQuery] string? city,
        [FromQuery] string? state,
        [FromQuery(Name = "postal-code")] string? postalCode,
        [FromQuery] int _count = 50,
        CancellationToken ct = default)
    {
        _count = ClampPageSize(_count, 200);
        var locations = new List<FhirResource>();

        if (!string.IsNullOrEmpty(organization))
        {
            var npi = organization.Replace("Organization/", "", StringComparison.Ordinal);
            var nppes = await LookupNppesAsync(npi, ct);
            if (nppes is not null)
            {
                for (var i = 0; i < nppes.Addresses.Count; i++)
                    locations.Add(ProviderDirectoryMapper.MapNppesToLocation(nppes, i));
            }
        }
        else
        {
            var results = await SearchNppesAsync(new Dictionary<string, string?>
            {
                ["city"] = city,
                ["state"] = state,
                ["postal_code"] = postalCode,
                ["limit"] = _count.ToString()
            }, ct);

            foreach (var r in results)
            {
                for (var i = 0; i < r.Addresses.Count; i++)
                    locations.Add(ProviderDirectoryMapper.MapNppesToLocation(r, i));
            }
        }

        return Ok(ProviderDirectoryMapper.CreateSearchBundle("Location", locations));
    }

    // ── NPPES HTTP Integration ───────────────────────────────────────────────

    private async Task<NppesResult?> LookupNppesAsync(string npi, CancellationToken ct)
    {
        if (!ProviderDirectoryMapper.ValidateNpi(npi))
        {
            _logger.LogWarning("Invalid NPI format: {Npi}", SanitizeForLog(npi));
            return null;
        }

        try
        {
            var response = await _httpClient.GetAsync($"?version=2.1&number={npi}", ct);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<NppesResponse>(JsonOptions, ct);
            if (data is null || data.ResultCount == 0 || data.Results is null || data.Results.Count == 0)
                return null;

            return data.Results[0];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NPPES lookup failed for NPI {Npi}", SanitizeForLog(npi));
            throw;
        }
    }

    // ── Provider Verification Integration ──────────────────────────────────────

    private async Task<ProviderVerificationSummary?> GetVerificationSummaryAsync(
        string npi, CancellationToken ct)
    {
        try
        {
            var response = await _verificationClient.GetAsync(
                $"api/v1/providers/{Uri.EscapeDataString(npi)}/integrity-score?tier=Basic", ct);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ProviderIntegrityResponse>(
                    cancellationToken: ct);

                if (result != null)
                {
                    return new ProviderVerificationSummary
                    {
                        IntegrityScore = result.CompositeScore,
                        Rating = result.Rating,
                        IsExcluded = string.Equals(result.Status, "Excluded", StringComparison.OrdinalIgnoreCase),
                        ExclusionSource = result.Flags?
                            .FirstOrDefault(f => f.Code == "EXCLUDED")?.Source,
                        Status = result.Status,
                    };
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "Provider Verification Service unavailable for NPI {Npi} — returning without enrichment",
                SanitizeForLog(npi));
        }

        return null;
    }

    private async Task<IReadOnlyList<NppesResult>> SearchNppesAsync(
        Dictionary<string, string?> queryParams,
        CancellationToken ct)
    {
        var query = new List<string> { "version=2.1" };
        foreach (var (key, value) in queryParams)
        {
            if (!string.IsNullOrEmpty(value))
                query.Add($"{key}={Uri.EscapeDataString(value)}");
        }

        try
        {
            var response = await _httpClient.GetAsync($"?{string.Join('&', query)}", ct);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<NppesResponse>(JsonOptions, ct);
            return data?.Results ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NPPES search failed");
            throw;
        }
    }
}
