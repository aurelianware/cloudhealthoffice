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
    /// — Practitioner; capability 5.8 — PractitionerRole; capability 5.9
    /// — Organization). Thin wrapper over
    /// <see cref="FhirControllerBase.ProxyUpstreamServiceAsync"/> — the
    /// shared status-translation logic was extracted in capability BP 5.8
    /// so both this controller and the new InsurancePlanController call
    /// the same helper. Resource label flows into structured-log fields
    /// so operators can distinguish proxy failures by resource type.
    /// </summary>
    private Task<IActionResult> ProxyProviderServiceAsync(
        string resourceLabel,
        string path,
        CancellationToken ct)
        => ProxyUpstreamServiceAsync(
            _providerServiceClient,
            "provider-service",
            resourceLabel,
            path,
            _logger,
            ct);

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
