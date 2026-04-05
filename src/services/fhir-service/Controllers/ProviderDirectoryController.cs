using System.Net.Http.Json;
using System.Text.Json;
using FhirService.Mappers;
using FhirService.Models;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Provider Directory API controller — exposes FHIR R4 provider directory resources
/// (Practitioner, PractitionerRole, Organization, Location) backed by NPPES data
/// fetched from provider-service via HTTP.
///
/// Port of the TypeScript provider-directory-api.ts.
/// </summary>
[Route("fhir/r4")]
public class ProviderDirectoryController : FhirControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _verificationClient;
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
        _logger = logger;
    }

    // ── Practitioner ─────────────────────────────────────────────────────────

    /// <summary>GET /fhir/r4/Practitioner/{id} — read Practitioner by NPI</summary>
    [HttpGet("Practitioner/{id}")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> ReadPractitioner(string id, CancellationToken ct)
    {
        var nppes = await LookupNppesAsync(id, ct);
        if (nppes is null || nppes.EnumerationType != "NPI-1")
            return FhirNotFound("Practitioner", id);

        var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(nppes);

        var verification = await GetVerificationSummaryAsync(id, ct);
        if (verification != null)
            ProviderDirectoryMapper.EnrichWithVerification(practitioner, verification);

        return Ok(practitioner);
    }

    /// <summary>GET /fhir/r4/Practitioner?npi=&amp;given=&amp;family=&amp;... — search Practitioners</summary>
    [HttpGet("Practitioner")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> SearchPractitioners(
        [FromQuery] string? npi,
        [FromQuery] string? given,
        [FromQuery] string? family,
        [FromQuery] string? city,
        [FromQuery] string? state,
        [FromQuery(Name = "postal-code")] string? postalCode,
        [FromQuery] string? specialty,
        [FromQuery] int _count = 50,
        [FromQuery] int _page = 1,
        CancellationToken ct = default)
    {
        _count = ClampPageSize(_count, 200);

        if (!string.IsNullOrEmpty(npi))
        {
            var nppes = await LookupNppesAsync(npi, ct);
            if (nppes is null || nppes.EnumerationType != "NPI-1")
                return Ok(ProviderDirectoryMapper.CreateSearchBundle("Practitioner", []));

            var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(nppes);
            return Ok(ProviderDirectoryMapper.CreateSearchBundle("Practitioner", [practitioner]));
        }

        var results = await SearchNppesAsync(new Dictionary<string, string?>
        {
            ["first_name"] = given,
            ["last_name"] = family,
            ["city"] = city,
            ["state"] = state,
            ["postal_code"] = postalCode,
            ["taxonomy_description"] = specialty,
            ["enumeration_type"] = "NPI-1",
            ["limit"] = _count.ToString(),
            ["skip"] = ((_page - 1) * _count).ToString()
        }, ct);

        var practitioners = results
            .Where(r => r.EnumerationType == "NPI-1")
            .Select(ProviderDirectoryMapper.MapNppesToPractitioner)
            .Cast<FhirResource>()
            .ToList();

        return Ok(ProviderDirectoryMapper.CreateSearchBundle("Practitioner", practitioners));
    }

    // ── Organization ─────────────────────────────────────────────────────────

    /// <summary>GET /fhir/r4/Organization/{id} — read Organization by NPI</summary>
    [HttpGet("Organization/{id}")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> ReadOrganization(string id, CancellationToken ct)
    {
        var nppes = await LookupNppesAsync(id, ct);
        if (nppes is null || nppes.EnumerationType != "NPI-2")
            return FhirNotFound("Organization", id);

        var organization = ProviderDirectoryMapper.MapNppesToOrganization(nppes);
        return Ok(organization);
    }

    /// <summary>GET /fhir/r4/Organization?npi=&amp;name=&amp;... — search Organizations</summary>
    [HttpGet("Organization")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> SearchOrganizations(
        [FromQuery] string? npi,
        [FromQuery] string? name,
        [FromQuery] string? city,
        [FromQuery] string? state,
        [FromQuery(Name = "postal-code")] string? postalCode,
        [FromQuery] int _count = 50,
        [FromQuery] int _page = 1,
        CancellationToken ct = default)
    {
        _count = ClampPageSize(_count, 200);

        if (!string.IsNullOrEmpty(npi))
        {
            var nppes = await LookupNppesAsync(npi, ct);
            if (nppes is null || nppes.EnumerationType != "NPI-2")
                return Ok(ProviderDirectoryMapper.CreateSearchBundle("Organization", []));

            var org = ProviderDirectoryMapper.MapNppesToOrganization(nppes);
            return Ok(ProviderDirectoryMapper.CreateSearchBundle("Organization", [org]));
        }

        var results = await SearchNppesAsync(new Dictionary<string, string?>
        {
            ["organization_name"] = name,
            ["city"] = city,
            ["state"] = state,
            ["postal_code"] = postalCode,
            ["enumeration_type"] = "NPI-2",
            ["limit"] = _count.ToString(),
            ["skip"] = ((_page - 1) * _count).ToString()
        }, ct);

        var organizations = results
            .Where(r => r.EnumerationType == "NPI-2")
            .Select(ProviderDirectoryMapper.MapNppesToOrganization)
            .Cast<FhirResource>()
            .ToList();

        return Ok(ProviderDirectoryMapper.CreateSearchBundle("Organization", organizations));
    }

    // ── PractitionerRole ─────────────────────────────────────────────────────

    /// <summary>GET /fhir/r4/PractitionerRole/{id} — read PractitionerRole by practitioner NPI</summary>
    [HttpGet("PractitionerRole/{id}")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> ReadPractitionerRole(string id, CancellationToken ct)
    {
        // ID format: {NPI}-role
        var npi = id.EndsWith("-role", StringComparison.Ordinal) ? id[..^5] : id;
        var nppes = await LookupNppesAsync(npi, ct);
        if (nppes is null || nppes.EnumerationType != "NPI-1")
            return FhirNotFound("PractitionerRole", id);

        var role = ProviderDirectoryMapper.MapNppesToPractitionerRole(nppes);
        // Note: PractitionerRole does not get verification enrichment directly —
        // the linked Practitioner resource carries the verification metadata.
        return Ok(role);
    }

    /// <summary>GET /fhir/r4/PractitionerRole?practitioner=&amp;specialty=&amp;... — search PractitionerRoles</summary>
    [HttpGet("PractitionerRole")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> SearchPractitionerRoles(
        [FromQuery] string? practitioner,
        [FromQuery] string? organization,
        [FromQuery] string? specialty,
        [FromQuery] int _count = 50,
        CancellationToken ct = default)
    {
        _count = ClampPageSize(_count, 200);
        var roles = new List<FhirResource>();

        if (!string.IsNullOrEmpty(practitioner))
        {
            var npi = practitioner.Replace("Practitioner/", "", StringComparison.Ordinal);
            var nppes = await LookupNppesAsync(npi, ct);
            if (nppes is not null && nppes.EnumerationType == "NPI-1")
            {
                var orgRef = !string.IsNullOrEmpty(organization)
                    ? new FhirReference { Reference = organization }
                    : null;
                roles.Add(ProviderDirectoryMapper.MapNppesToPractitionerRole(nppes, orgRef));
            }
        }
        else if (!string.IsNullOrEmpty(specialty))
        {
            var results = await SearchNppesAsync(new Dictionary<string, string?>
            {
                ["taxonomy_description"] = specialty,
                ["enumeration_type"] = "NPI-1",
                ["limit"] = _count.ToString()
            }, ct);

            foreach (var r in results.Where(r => r.EnumerationType == "NPI-1"))
                roles.Add(ProviderDirectoryMapper.MapNppesToPractitionerRole(r));
        }

        return Ok(ProviderDirectoryMapper.CreateSearchBundle("PractitionerRole", roles));
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
