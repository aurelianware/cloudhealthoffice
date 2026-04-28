using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// FHIR R4 Organization read + search endpoint (capability 5.9).
/// provider-service is the canonical authority on the projection;
/// fhir-service proxies <c>/fhir/r4/Organization/*</c> requests here so
/// CHO retains a single FHIR façade for external consumers while each
/// domain service owns its own projection.
///
/// <para>
/// Two source entities project to FHIR Organization (Decision from 5.9
/// plan-phase):
/// <list type="bullet">
///   <item><see cref="Organization"/> network entity (capability 5.3) —
///   FHIR <c>type=ins</c>. Addressed by its chain-key
///   <see cref="Organization.OrganizationId"/>.</item>
///   <item><see cref="Provider"/> with
///   <see cref="ProviderType.Organization"/> — FHIR <c>type=prov</c>.
///   Addressed by <see cref="Provider.NPI"/> (NPI-2, 10 digits).</item>
/// </list>
/// </para>
///
/// <para>
/// <c>GET /fhir/Organization/{id}</c> discriminates by shape (Decision 6):
/// if <paramref name="id"/> matches the 10-digit NPI regex, resolve as
/// Provider-as-Org; otherwise resolve as Organization by chain key. NPI
/// check wins on 10-digit input even if a tenant has authored an
/// OrganizationId that is 10 digits (unusual, visibly NPI-shaped to any
/// reader; documented in XML doc here).
/// </para>
///
/// <para>
/// <c>GET /fhir/Organization</c> search (Decision 7 — Option 7a, single
/// endpoint parameter-discriminated): <c>?npi=</c> returns Provider-as-Org;
/// <c>?identifier=ORG:{orgId}</c> returns Organization entity; other
/// parameters (<c>name</c>, <c>city</c>, <c>state</c>, <c>postal-code</c>)
/// search both source entities and merge results.
/// </para>
///
/// <para>
/// Tenant scoping is honored via the existing
/// <see cref="Middleware.TenantMiddleware"/> mechanism. Public CMS-0057-F
/// unauthenticated access is a separate capability (5.19).
/// </para>
/// </summary>
[ApiController]
[Route("fhir")]
public class FhirOrganizationController : ControllerBase
{
    private const string FhirContentType = "application/fhir+json";
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private static readonly Regex NpiPattern = new(@"^\d{10}$", RegexOptions.Compiled);

    private readonly IProviderRepository _providerRepository;
    private readonly IOrganizationRepository _organizationRepository;
    private readonly IFhirOrganizationProjector _projector;
    private readonly ILogger<FhirOrganizationController> _logger;

    public FhirOrganizationController(
        IProviderRepository providerRepository,
        IOrganizationRepository organizationRepository,
        IFhirOrganizationProjector projector,
        ILogger<FhirOrganizationController> logger)
    {
        _providerRepository = providerRepository;
        _organizationRepository = organizationRepository;
        _projector = projector;
        _logger = logger;
    }

    /// <summary>
    /// FHIR Organization read. The <paramref name="id"/> is shape-detected
    /// (Decision 6):
    /// <list type="bullet">
    ///   <item>10-digit numeric string → NPI-2 → look up Provider with
    ///   <c>ProviderType=Organization</c>.</item>
    ///   <item>Anything else → OrganizationId chain key → look up
    ///   Organization entity.</item>
    ///   <item>If neither path resolves → 404 OperationOutcome.</item>
    /// </list>
    /// NPI check wins on 10-digit input even if a tenant has authored an
    /// OrganizationId that is 10 digits.
    /// </summary>
    [HttpGet("Organization/{id}")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> ReadOrganization(string id, CancellationToken ct)
    {
        if (NpiPattern.IsMatch(id))
        {
            // Try Provider-as-Org path first.
            var provider = await _providerRepository.GetByNPIAsync(id);
            if (provider != null && provider.ProviderType == ProviderType.Organization)
            {
                var resource = _projector.Project(provider);
                if (resource != null)
                {
                    return FhirContent(resource);
                }
            }
            // 10-digit id matched NPI pattern but no qualifying provider
            // found; fall through to 404 rather than retrying as an
            // OrganizationId (NPI wins per Decision 6).
            return FhirOperationOutcome(404, "not-found", $"Organization/{SanitizeForLog(id)} not found.");
        }

        // Non-NPI id → try Organization entity chain key.
        var network = await _organizationRepository.GetByIdAsync(id);
        if (network != null)
        {
            var resource = _projector.Project(network);
            if (resource != null) return FhirContent(resource);
        }

        return FhirOperationOutcome(404, "not-found", $"Organization/{SanitizeForLog(id)} not found.");
    }

    /// <summary>
    /// FHIR Organization search (Decision 7 — Option 7a). Parameter
    /// semantics:
    /// <list type="bullet">
    ///   <item><c>npi</c> — exact NPI-2 match; returns Provider-as-Org
    ///   only.</item>
    ///   <item><c>identifier=ORG:{orgId}</c> — chain-key lookup; returns
    ///   Organization entity only.</item>
    ///   <item><c>name</c>, <c>city</c>, <c>state</c>,
    ///   <c>postal-code</c> — fuzzy filters applied to both source
    ///   entities; results are merged with <c>type</c> discriminating in
    ///   the Bundle.</item>
    ///   <item><c>type=prov|ins</c> — when supplied, restricts the search
    ///   to only that source entity type.</item>
    /// </list>
    /// Returns a FHIR <c>Bundle</c> of type <c>searchset</c>.
    /// </summary>
    [HttpGet("Organization")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> SearchOrganizations(
        [FromQuery] string? npi,
        [FromQuery] string? identifier,
        [FromQuery] string? name,
        [FromQuery] string? city,
        [FromQuery] string? state,
        [FromQuery(Name = "postal-code")] string? postalCode,
        [FromQuery] string? type,
        [FromQuery] int _count = DefaultPageSize,
        [FromQuery] int _page = 1,
        CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(_count, 1, MaxPageSize);
        var page = Math.Max(1, _page);

        var entries = new JsonArray();

        try
        {
            // ── NPI-specific path ─────────────────────────────────────
            if (!string.IsNullOrEmpty(npi))
            {
                if (!NpiPattern.IsMatch(npi))
                {
                    return FhirOperationOutcome(400, "invalid",
                        $"npi '{SanitizeForLog(npi)}' is not a valid 10-digit NPI.");
                }

                var provider = await _providerRepository.GetByNPIAsync(npi);
                if (provider != null && provider.ProviderType == ProviderType.Organization)
                {
                    var projected = _projector.Project(provider);
                    if (projected != null) entries.Add(WrapEntry(projected));
                }

                return BundleResult(entries);
            }

            // ── identifier=ORG:{orgId} path ───────────────────────────
            var orgIdFromIdentifier = ParseOrgIdentifier(identifier);
            if (orgIdFromIdentifier != null)
            {
                var network = await _organizationRepository.GetByIdAsync(orgIdFromIdentifier);
                if (network != null)
                {
                    var projected = _projector.Project(network);
                    if (projected != null) entries.Add(WrapEntry(projected));
                }

                return BundleResult(entries);
            }

            // ── merged search across both source entities ─────────────
            // When ?type=prov, search only Provider-as-Org.
            // When ?type=ins, search only Organization entity.
            // Without type, search both and merge.
            var wantProv = string.IsNullOrEmpty(type)
                || string.Equals(type, "prov", StringComparison.OrdinalIgnoreCase);
            var wantIns = string.IsNullOrEmpty(type)
                || string.Equals(type, "ins", StringComparison.OrdinalIgnoreCase);

            if (!wantProv && !wantIns)
            {
                // Caller supplied an unrecognized type value — return empty
                // Bundle; FHIR search unknown-type behavior per FHIR spec is
                // an empty result (not an error).
                return BundleResult(entries);
            }

            if (wantProv)
            {
                ct.ThrowIfCancellationRequested();
                // When merging both source entities, limit the provider pass to
                // at most pageSize/2 entries so the combined total never exceeds
                // pageSize (_count must bound the page size per FHIR convention).
                var provPageSize = wantIns ? Math.Max(1, pageSize / 2) : pageSize;
                var providers = await _providerRepository.SearchAsync(
                    name: name,
                    specialty: null,
                    zipCode: postalCode,
                    state: state,
                    planId: null,
                    lineOfBusiness: null,
                    providerType: ProviderType.Organization,
                    acceptingNewPatients: null,
                    page: page,
                    pageSize: provPageSize,
                    city: city);

                foreach (var p in providers)
                {
                    if (p.ProviderType != ProviderType.Organization) continue;
                    var projected = _projector.Project(p);
                    if (projected != null) entries.Add(WrapEntry(projected));
                }
            }

            if (wantIns)
            {
                ct.ThrowIfCancellationRequested();
                var tenantId = HttpContext.Items["TenantId"] as string ?? string.Empty;
                if (!string.IsNullOrEmpty(tenantId))
                {
                    // IOrganizationRepository.ListAsync does not expose
                    // name/city/state/zip filters, so MatchesNetworkFilters is
                    // applied application-side. To avoid incorrect paging
                    // semantics (filtering after a page boundary means later
                    // matching rows on subsequent repository pages are silently
                    // skipped), iterate repository pages in order, skipping
                    // the first (page-1)*insSlots filtered matches and
                    // collecting up to insSlots results.
                    var insSlots = pageSize - entries.Count;
                    var filteredOffset = (page - 1) * insSlots;
                    var filteredSeen = 0;
                    var repositoryPage = 1;

                    while (entries.Count < pageSize)
                    {
                        ct.ThrowIfCancellationRequested();

                        var (networks, _) = await _organizationRepository.ListAsync(
                            networkType: null,
                            lineOfBusiness: null,
                            parentOrganizationId: null,
                            page: repositoryPage,
                            pageSize: pageSize);

                        if (networks.Count == 0) break;

                        foreach (var network in networks)
                        {
                            ct.ThrowIfCancellationRequested();

                            if (!MatchesNetworkFilters(network, name, city, state, postalCode)) continue;

                            if (filteredSeen < filteredOffset)
                            {
                                filteredSeen++;
                                continue;
                            }

                            var projected = _projector.Project(network);
                            if (projected != null)
                            {
                                entries.Add(WrapEntry(projected));
                                if (entries.Count >= pageSize) break;
                            }
                        }

                        if (networks.Count < pageSize) break;

                        repositoryPage++;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Organization search failed");
            return FhirOperationOutcome(500, "exception", "Organization search failed.");
        }

        return BundleResult(entries);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse an <c>identifier</c> parameter value that carries an
    /// OrganizationId chain key. Accepted forms:
    /// <list type="bullet">
    ///   <item><c>ORG:{orgId}</c> — CHO shorthand prefix.</item>
    ///   <item><c>urn:cho:network|{orgId}</c> — system|value form.</item>
    /// </list>
    /// Returns null when the parameter is blank or does not match any
    /// recognized OrganizationId pattern.
    /// </summary>
    private static string? ParseOrgIdentifier(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return null;

        // ORG:{orgId} shorthand
        if (identifier.StartsWith("ORG:", StringComparison.OrdinalIgnoreCase))
        {
            var value = identifier[4..];
            return string.IsNullOrEmpty(value) ? null : value;
        }

        // system|value form with CHO network system
        var pipe = identifier.IndexOf('|');
        if (pipe > 0)
        {
            var system = identifier[..pipe];
            var value = identifier[(pipe + 1)..];
            if (string.Equals(system, "urn:cho:network", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool MatchesNetworkFilters(
        Organization org,
        string? name, string? city, string? state, string? postalCode)
    {
        if (!string.IsNullOrEmpty(name)
            && !org.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Network Organizations carry address on ContactInfo; if no
        // ContactInfo, address filters don't match.
        if (!string.IsNullOrEmpty(city) || !string.IsNullOrEmpty(state) || !string.IsNullOrEmpty(postalCode))
        {
            var c = org.ContactInfo;
            if (c == null) return false;
            if (!string.IsNullOrEmpty(city)
                && !string.Equals(c.City, city, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(state)
                && !string.Equals(c.State, state, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(postalCode)
                && !(c.ZipCode?.StartsWith(postalCode, StringComparison.Ordinal) ?? false))
            {
                return false;
            }
        }

        return true;
    }

    private IActionResult BundleResult(JsonArray entries)
    {
        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = entries.Count,
            ["entry"] = entries,
        };
        return FhirContent(bundle);
    }

    private static JsonObject WrapEntry(JsonObject resource) => new()
    {
        // fullUrl is intentionally omitted (same rationale as 5.7/5.8):
        // under the proxy hop Request.Host is the internal provider-service
        // hostname and would leak into the Bundle.
        ["resource"] = resource,
        ["search"] = new JsonObject { ["mode"] = "match" },
    };

    private static IActionResult FhirContent(JsonObject node) => new ContentResult
    {
        ContentType = FhirContentType,
        Content = node.ToJsonString(),
        StatusCode = 200,
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
