using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// FHIR R4 Practitioner read + search endpoint (capability 5.7).
/// provider-service is the canonical authority on the projection;
/// fhir-service proxies <c>/fhir/r4/Practitioner/*</c> requests here so
/// CHO retains a single FHIR façade for external consumers while each
/// domain service owns its own projection (mirrors member-service's
/// <c>MembersController.GetFhirPatient</c>).
///
/// <para>
/// Tenant scoping per Decision 5a: requests honor the existing
/// <see cref="Middleware.TenantMiddleware"/> mechanism. Authenticated /
/// header-scoped callers see their tenant's providers only. Public
/// CMS-0057-F access is a separate capability (5.19).
/// </para>
/// </summary>
[ApiController]
[Route("fhir")]
public class FhirPractitionerController : ControllerBase
{
    private const string FhirContentType = "application/fhir+json";
    private const string NpiSystemToken = "http://hl7.org/fhir/sid/us-npi";
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private static readonly Regex NpiPattern = new(@"^\d{10}$", RegexOptions.Compiled);

    private readonly IProviderRepository _repository;
    private readonly IFhirPractitionerProjector _projector;
    private readonly ILogger<FhirPractitionerController> _logger;

    public FhirPractitionerController(
        IProviderRepository repository,
        IFhirPractitionerProjector projector,
        ILogger<FhirPractitionerController> logger)
    {
        _repository = repository;
        _projector = projector;
        _logger = logger;
    }

    /// <summary>
    /// FHIR Practitioner read by NPI. The path segment is the FHIR
    /// resource <c>id</c>, which capability 5.7 maps to NPI per
    /// Decision 3.
    /// </summary>
    [HttpGet("Practitioner/{npi}")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> ReadPractitioner(string npi, CancellationToken ct)
    {
        if (!NpiPattern.IsMatch(npi))
        {
            return FhirOperationOutcome(400, "invalid", $"NPI '{SanitizeForLog(npi)}' is not a 10-digit identifier.");
        }

        var provider = await _repository.GetByNPIAsync(npi);
        if (provider == null || provider.ProviderType != ProviderType.Individual)
        {
            return FhirOperationOutcome(404, "not-found", $"Practitioner/{npi} not found.");
        }

        var integrity = ProviderIntegrityProjection.FromProvider(provider);
        var resource = _projector.Project(provider, integrity);
        if (resource == null)
        {
            // Defensive: projector returns null for ProviderType.Organization,
            // which is filtered above. Treat as 404 so the surface stays
            // consistent if a future Provider value sneaks past the type check.
            return FhirOperationOutcome(404, "not-found", $"Practitioner/{npi} not found.");
        }

        return new ContentResult
        {
            ContentType = FhirContentType,
            Content = resource.ToJsonString(),
            StatusCode = 200
        };
    }

    /// <summary>
    /// FHIR Practitioner search. Honors FHIR R4 search parameter names
    /// (<c>given</c>, <c>family</c>, <c>city</c>, <c>state</c>,
    /// <c>postal-code</c>, <c>specialty</c>, <c>identifier</c>) plus the
    /// shorthand <c>npi</c> alias the existing fhir-service controller
    /// uses. Returns a FHIR <c>Bundle</c> of type <c>searchset</c>.
    /// </summary>
    [HttpGet("Practitioner")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> SearchPractitioners(
        [FromQuery] string? npi,
        [FromQuery] string? identifier,
        [FromQuery] string? given,
        [FromQuery] string? family,
        [FromQuery] string? city,
        [FromQuery] string? state,
        [FromQuery(Name = "postal-code")] string? postalCode,
        [FromQuery] string? specialty,
        [FromQuery] int _count = DefaultPageSize,
        [FromQuery] int _page = 1,
        CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(_count, 1, MaxPageSize);
        var page = Math.Max(1, _page);

        // Resolve identifier=NPI:value or identifier=value to the npi
        // shortcut. FHIR token parameters use system|value or value alone.
        // An `identifier` whose system is something OTHER than NPI is a
        // caller error for this directory — we only index by NPI today —
        // so reject with 400 rather than silently falling back to a
        // broad search (FHIR token semantics say: don't ignore filters).
        var resolvedNpi = npi;
        if (string.IsNullOrEmpty(resolvedNpi) && !string.IsNullOrEmpty(identifier))
        {
            resolvedNpi = ParseNpiIdentifier(identifier);
            if (string.IsNullOrEmpty(resolvedNpi))
            {
                return FhirOperationOutcome(400, "invalid",
                    $"identifier '{SanitizeForLog(identifier)}' is not a recognized NPI token; supply system http://hl7.org/fhir/sid/us-npi or no system.");
            }
        }

        if (!string.IsNullOrEmpty(resolvedNpi))
        {
            if (!NpiPattern.IsMatch(resolvedNpi))
            {
                return FhirOperationOutcome(400, "invalid",
                    $"identifier '{SanitizeForLog(identifier ?? npi ?? string.Empty)}' is not a valid NPI.");
            }

            var single = await _repository.GetByNPIAsync(resolvedNpi);
            var bundleEntries = new JsonArray();
            if (single != null && single.ProviderType == ProviderType.Individual)
            {
                var integrity = ProviderIntegrityProjection.FromProvider(single);
                var projected = _projector.Project(single, integrity);
                if (projected != null) bundleEntries.Add(WrapEntry(projected));
            }
            return BundleResult(bundleEntries);
        }

        IEnumerable<Provider> results;
        try
        {
            results = await _repository.SearchAsync(
                name: null,
                specialty: specialty,
                zipCode: postalCode,
                state: state,
                planId: null,
                lineOfBusiness: null,
                providerType: ProviderType.Individual,
                acceptingNewPatients: null,
                page: page,
                pageSize: pageSize,
                firstName: given,
                lastName: family,
                city: city);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Practitioner search failed");
            return FhirOperationOutcome(500, "exception", "Practitioner search failed.");
        }

        var entries = new JsonArray();
        foreach (var provider in results)
        {
            if (provider.ProviderType != ProviderType.Individual) continue;
            var integrity = ProviderIntegrityProjection.FromProvider(provider);
            var projected = _projector.Project(provider, integrity);
            if (projected != null) entries.Add(WrapEntry(projected));
        }
        return BundleResult(entries);
    }

    // ── helpers ────────────────────────────────────────────────────────

    private IActionResult BundleResult(JsonArray entries)
    {
        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = entries.Count,
            ["entry"] = entries
        };
        return new ContentResult
        {
            ContentType = FhirContentType,
            Content = bundle.ToJsonString(),
            StatusCode = 200
        };
    }

    private static JsonObject WrapEntry(JsonObject resource)
    {
        // Bundle.entry.fullUrl is OPTIONAL in FHIR R4 (see
        // http://hl7.org/fhir/R4/bundle-definitions.html#Bundle.entry.fullUrl).
        // We deliberately omit it: this controller is reached either
        // directly OR via fhir-service's Practitioner proxy, in which case
        // HttpContext.Request.Host is the *internal* provider-service
        // hostname and would leak into the response. fhir-service's proxy
        // does not yet rewrite forwarded headers and we don't want
        // Bundle entries to look different depending on which path was
        // taken. Consumers can construct Practitioner/{id} references
        // themselves using the resource's `id` field.
        return new JsonObject
        {
            ["resource"] = resource,
            ["search"] = new JsonObject { ["mode"] = "match" }
        };
    }

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
                    ["diagnostics"] = diagnostics
                }
            }
        };
        return new ContentResult
        {
            ContentType = FhirContentType,
            Content = outcome.ToJsonString(),
            StatusCode = status
        };
    }

    private static string? ParseNpiIdentifier(string identifier)
    {
        // FHIR token parameter forms:
        //   identifier=12345                 → bare value
        //   identifier=NPI|12345             → system|value (treat NPI as alias)
        //   identifier=http://hl7.org/...|12345 → fully-qualified system|value
        //   identifier=NPI:12345             → legacy shorthand the existing fhir-service uses
        if (string.IsNullOrEmpty(identifier)) return null;

        var pipe = identifier.IndexOf('|');
        if (pipe >= 0)
        {
            var system = identifier[..pipe];
            var value = identifier[(pipe + 1)..];
            if (string.IsNullOrEmpty(system)
                || string.Equals(system, NpiSystemToken, StringComparison.OrdinalIgnoreCase)
                || string.Equals(system, "NPI", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
            return null;
        }

        var colon = identifier.IndexOf(':');
        if (colon > 0)
        {
            var system = identifier[..colon];
            var value = identifier[(colon + 1)..];
            if (string.Equals(system, "NPI", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return identifier;
    }

    private static string SanitizeForLog(string value)
        => value.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal);
}
