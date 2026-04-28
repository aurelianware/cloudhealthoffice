using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// FHIR R4 PractitionerRole read + search endpoint (capability 5.8).
/// provider-service is the canonical authority on the projection;
/// fhir-service proxies <c>/fhir/r4/PractitionerRole/*</c> requests here
/// so CHO retains a single FHIR façade for external consumers while each
/// domain service owns its own projection (mirrors
/// <see cref="FhirPractitionerController"/> from capability 5.7).
///
/// <para>
/// Tenant scoping is honored via the existing
/// <see cref="Middleware.TenantMiddleware"/> mechanism (Decision 8 of the
/// 5.8 plan-phase). Public CMS-0057-F unauthenticated access is a
/// separate capability (5.19).
/// </para>
///
/// <para>
/// Pagination is page-based to match
/// <see cref="FhirPractitionerController"/>; the cursor-based shape used
/// by the operational roster API is intentionally not surfaced here —
/// FHIR clients expect <c>_count</c> + a paged Bundle.
/// </para>
/// </summary>
[ApiController]
[Route("fhir")]
public class FhirPractitionerRoleController : ControllerBase
{
    private const string FhirContentType = "application/fhir+json";
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private static readonly Regex NpiPattern = new(@"^\d{10}$", RegexOptions.Compiled);
    private static readonly Regex NetworkIdPattern = new(@"^[A-Za-z0-9\-]{1,64}$", RegexOptions.Compiled);

    private readonly IProviderRepository _providerRepository;
    private readonly IOrganizationRepository _organizationRepository;
    private readonly IFhirPractitionerRoleProjector _projector;
    private readonly ILogger<FhirPractitionerRoleController> _logger;

    public FhirPractitionerRoleController(
        IProviderRepository providerRepository,
        IOrganizationRepository organizationRepository,
        IFhirPractitionerRoleProjector projector,
        ILogger<FhirPractitionerRoleController> logger)
    {
        _providerRepository = providerRepository;
        _organizationRepository = organizationRepository;
        _projector = projector;
        _logger = logger;
    }

    /// <summary>
    /// FHIR PractitionerRole read by composite-tuple id. The id format is
    /// <c>{npi}-{lobInt}-{yyyymmdd}-{networkId}</c> (capability 5.8
    /// Decision 6) — decoded back to the tuple, then the matching
    /// <see cref="NetworkParticipation"/> is located on the head Active
    /// version of the linked provider.
    /// </summary>
    [HttpGet("PractitionerRole/{id}")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> ReadPractitionerRole(string id, CancellationToken ct)
    {
        var decoded = _projector.DecodeId(id);
        if (decoded is null)
        {
            return FhirOperationOutcome(404, "not-found", $"PractitionerRole/{SanitizeForLog(id)} not found.");
        }

        var provider = await _providerRepository.GetByNPIAsync(decoded.Npi);
        if (provider == null
            || provider.ProviderType != ProviderType.Individual
            || provider.VersionState != ProviderVersionState.Active
            || provider.Status != ProviderStatus.Active)
        {
            return FhirOperationOutcome(404, "not-found", $"PractitionerRole/{SanitizeForLog(id)} not found.");
        }

        var participation = provider.NetworkParticipations.FirstOrDefault(p =>
            p.NetworkId == decoded.NetworkId
            && p.LineOfBusiness == decoded.LineOfBusiness
            // SpecifyKind, not ToUniversalTime — Cosmos / Mongo
            // round-trip dates as Kind=Unspecified and ToUniversalTime
            // would shift them by the host TZ offset.
            && DateTime.SpecifyKind(p.EffectiveDate, DateTimeKind.Utc).Date == decoded.EffectiveDate.Date);
        if (participation is null)
        {
            return FhirOperationOutcome(404, "not-found", $"PractitionerRole/{SanitizeForLog(id)} not found.");
        }

        var network = await _organizationRepository.GetByIdAsync(decoded.NetworkId);
        var resource = _projector.Project(participation, provider, network);
        if (resource == null)
        {
            return FhirOperationOutcome(404, "not-found", $"PractitionerRole/{SanitizeForLog(id)} not found.");
        }

        return new ContentResult
        {
            ContentType = FhirContentType,
            Content = resource.ToJsonString(),
            StatusCode = 200,
        };
    }

    /// <summary>
    /// FHIR PractitionerRole search. Honors the FHIR R4 search parameter
    /// names <c>practitioner</c>, <c>organization</c>, <c>specialty</c>,
    /// and <c>_count</c> from the existing fhir-service surface; adds
    /// <c>_page</c> for page-based pagination. Returns a FHIR
    /// <c>Bundle</c> of type <c>searchset</c>.
    /// </summary>
    [HttpGet("PractitionerRole")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> SearchPractitionerRoles(
        [FromQuery] string? practitioner,
        [FromQuery] string? organization,
        [FromQuery] string? specialty,
        [FromQuery] int _count = DefaultPageSize,
        [FromQuery] int _page = 1,
        CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(_count, 1, MaxPageSize);
        var page = Math.Max(1, _page);

        var practitionerNpi = ParseReference(practitioner, "Practitioner/");
        var organizationId = ParseReference(organization, "Organization/");

        // Reject malformed reference inputs early — silent fallback would
        // produce an unbounded search the caller didn't request.
        if (!string.IsNullOrEmpty(practitioner) && string.IsNullOrEmpty(practitionerNpi))
        {
            return FhirOperationOutcome(400, "invalid",
                $"practitioner '{SanitizeForLog(practitioner)}' is not a recognized Practitioner reference.");
        }
        if (!string.IsNullOrEmpty(practitionerNpi) && !NpiPattern.IsMatch(practitionerNpi))
        {
            return FhirOperationOutcome(400, "invalid",
                $"practitioner '{SanitizeForLog(practitioner!)}' does not contain a 10-digit NPI.");
        }
        if (!string.IsNullOrEmpty(organization) && string.IsNullOrEmpty(organizationId))
        {
            return FhirOperationOutcome(400, "invalid",
                $"organization '{SanitizeForLog(organization)}' is not a recognized Organization reference.");
        }
        if (!string.IsNullOrEmpty(organizationId) && !NetworkIdPattern.IsMatch(organizationId))
        {
            return FhirOperationOutcome(400, "invalid",
                $"organization '{SanitizeForLog(organization!)}' references an unsupported network id shape.");
        }

        var entries = new JsonArray();

        try
        {
            if (!string.IsNullOrEmpty(practitionerNpi))
            {
                await BuildEntriesByPractitionerAsync(
                    practitionerNpi,
                    organizationId,
                    specialty,
                    page,
                    pageSize,
                    entries,
                    ct);
            }
            else if (!string.IsNullOrEmpty(organizationId))
            {
                await BuildEntriesByOrganizationAsync(
                    organizationId,
                    specialty,
                    page,
                    pageSize,
                    entries,
                    ct);
            }
            else if (!string.IsNullOrEmpty(specialty))
            {
                await BuildEntriesBySpecialtyAsync(
                    specialty,
                    page,
                    pageSize,
                    entries,
                    ct);
            }
            // No filters supplied → empty Bundle. FHIR doesn't require
            // Bundle.entry to be populated and an unbounded directory
            // dump is not what callers expect; mirror the existing
            // fhir-service behavior of "must specify at least one of
            // practitioner / organization / specialty".
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PractitionerRole search failed");
            return FhirOperationOutcome(500, "exception", "PractitionerRole search failed.");
        }

        return BundleResult(entries);
    }

    // ── search helpers ────────────────────────────────────────────────

    private async Task BuildEntriesByPractitionerAsync(
        string npi,
        string? organizationFilter,
        string? specialtyFilter,
        int page,
        int pageSize,
        JsonArray entries,
        CancellationToken ct)
    {
        // GetByNPIAsync / GetByIdAsync don't take a CancellationToken
        // today (cross-service cleanup tracked separately). Honor the
        // caller's cancellation cooperatively at the helper boundary so
        // an aborted request short-circuits before the next repo hop.
        ct.ThrowIfCancellationRequested();
        var provider = await _providerRepository.GetByNPIAsync(npi);
        if (provider == null
            || provider.ProviderType != ProviderType.Individual
            || provider.VersionState != ProviderVersionState.Active
            || provider.Status != ProviderStatus.Active)
        {
            return;
        }

        if (!MatchesSpecialty(provider, specialtyFilter)) return;

        var participations = provider.NetworkParticipations
            .Where(p => !string.IsNullOrEmpty(p.NetworkId))
            .Where(p => string.IsNullOrEmpty(organizationFilter) || p.NetworkId == organizationFilter)
            .OrderBy(p => p.NetworkId, StringComparer.Ordinal)
            .ThenBy(p => p.EffectiveDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var networkCache = new Dictionary<string, Organization?>(StringComparer.Ordinal);
        foreach (var participation in participations)
        {
            var network = await ResolveNetworkAsync(participation.NetworkId!, networkCache);
            var projected = _projector.Project(participation, provider, network);
            if (projected != null) entries.Add(WrapEntry(projected));
        }
    }

    private async Task BuildEntriesByOrganizationAsync(
        string networkId,
        string? specialtyFilter,
        int page,
        int pageSize,
        JsonArray entries,
        CancellationToken ct)
    {
        // Reuse the 5.4 roster repository query (Decision 8 — direct
        // repo call, skip INetworkRosterService). NetworkRosterQuery's
        // server-only fields (TenantId, NetworkId) are populated here;
        // the controller's TenantMiddleware sets the per-request tenant
        // context, which is read inside the repository.
        var query = new NetworkRosterQuery
        {
            TenantId = HttpContext.Items["TenantId"] as string ?? string.Empty,
            NetworkId = networkId,
            Specialty = specialtyFilter,
            PageSize = pageSize,
            Page = page,
        };
        if (string.IsNullOrEmpty(query.TenantId))
        {
            // The InMemory fake reads TenantId from query.TenantId and
            // would otherwise match all rows when it is empty. The real
            // Cosmos / Mongo repositories require TenantId and would
            // throw, so surface this as an empty result instead.
            return;
        }

        var skip = (page - 1) * pageSize;
        var rows = await _providerRepository.ListNetworkRosterAsync(
            query,
            NetworkRosterSort.NameAsc,
            skip,
            ct);

        var network = await _organizationRepository.GetByIdAsync(networkId);

        foreach (var provider in rows)
        {
            if (provider.ProviderType != ProviderType.Individual) continue;
            if (provider.VersionState != ProviderVersionState.Active) continue;
            if (provider.Status != ProviderStatus.Active) continue;

            // The repository EXISTS-clause guarantees ≥1 participation
            // with the requested NetworkId; emit one PractitionerRole
            // per matching participation so a provider with multiple
            // (e.g. different LOBs in the same network) appears once
            // per role.
            foreach (var participation in provider.NetworkParticipations)
            {
                if (participation.NetworkId != networkId) continue;
                var projected = _projector.Project(participation, provider, network);
                if (projected != null) entries.Add(WrapEntry(projected));
            }
        }
    }

    private async Task BuildEntriesBySpecialtyAsync(
        string specialty,
        int page,
        int pageSize,
        JsonArray entries,
        CancellationToken ct)
    {
        // Specialty-only search routes through IProviderRepository.SearchAsync
        // (the existing 5.7 search shape). Per provider, we emit one
        // PractitionerRole per network participation with a populated
        // NetworkId. SearchAsync / GetByIdAsync don't take a
        // CancellationToken today; honor cancellation cooperatively at
        // the helper boundary.
        ct.ThrowIfCancellationRequested();
        var providers = await _providerRepository.SearchAsync(
            name: null,
            specialty: specialty,
            zipCode: null,
            state: null,
            planId: null,
            lineOfBusiness: null,
            providerType: ProviderType.Individual,
            acceptingNewPatients: null,
            page: page,
            pageSize: pageSize);

        var networkCache = new Dictionary<string, Organization?>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (provider.ProviderType != ProviderType.Individual) continue;
            if (provider.VersionState != ProviderVersionState.Active) continue;
            if (provider.Status != ProviderStatus.Active) continue;

            foreach (var participation in provider.NetworkParticipations)
            {
                if (string.IsNullOrEmpty(participation.NetworkId)) continue;
                var network = await ResolveNetworkAsync(participation.NetworkId, networkCache);
                var projected = _projector.Project(participation, provider, network);
                if (projected != null) entries.Add(WrapEntry(projected));
            }
        }
    }

    private async Task<Organization?> ResolveNetworkAsync(
        string networkId,
        Dictionary<string, Organization?> cache)
    {
        if (cache.TryGetValue(networkId, out var cached)) return cached;
        var network = await _organizationRepository.GetByIdAsync(networkId);
        cache[networkId] = network;
        return network;
    }

    private static bool MatchesSpecialty(Provider provider, string? specialty)
    {
        if (string.IsNullOrEmpty(specialty)) return true;
        return (provider.PrimarySpecialty?.Contains(specialty, StringComparison.OrdinalIgnoreCase) ?? false)
            || (provider.TaxonomyCode?.Contains(specialty, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string? ParseReference(string? raw, string typePrefix)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        // Accept both "Practitioner/{id}" and bare "{id}" forms. FHIR
        // search reference parameters specify the typed form, but bare
        // values are common in the wild and the existing fhir-service
        // controller accepted them.
        if (raw.StartsWith(typePrefix, StringComparison.Ordinal))
        {
            var value = raw[typePrefix.Length..];
            return string.IsNullOrEmpty(value) ? null : value;
        }
        if (raw.Contains('/', StringComparison.Ordinal))
        {
            // A typed reference to a different resource type — reject.
            return null;
        }
        return raw;
    }

    // ── response helpers ──────────────────────────────────────────────

    private IActionResult BundleResult(JsonArray entries)
    {
        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = entries.Count,
            ["entry"] = entries,
        };
        return new ContentResult
        {
            ContentType = FhirContentType,
            Content = bundle.ToJsonString(),
            StatusCode = 200,
        };
    }

    private static JsonObject WrapEntry(JsonObject resource) => new()
    {
        // fullUrl is intentionally omitted for the same reason it is on
        // the Practitioner Bundle (5.7): under the proxy hop, Request.Host
        // is the internal provider-service hostname.
        ["resource"] = resource,
        ["search"] = new JsonObject { ["mode"] = "match" },
    };

    private IActionResult FhirOperationOutcome(int status, string code, string diagnostics)
    {
        var outcome = new JsonObject
        {
            ["resourceType"] = "OperationOutcome",
            ["issue"] = new JsonArray
            {
                new JsonObject
                {
                    ["severity"] = "error",
                    ["code"] = code,
                    ["diagnostics"] = diagnostics,
                },
            },
        };
        return new ContentResult
        {
            ContentType = FhirContentType,
            Content = outcome.ToJsonString(),
            StatusCode = status,
        };
    }

    private static string SanitizeForLog(string value)
        => value.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal);
}
