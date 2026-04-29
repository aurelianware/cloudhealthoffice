using System.Text.Json.Nodes;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// FHIR R4 Endpoint read + search (capability BP 5.9 — Plan Documents →
/// FHIR Endpoint projection). benefit-plan-service is the canonical
/// authority on the projection; fhir-service proxies
/// <c>/fhir/r4/Endpoint/*</c> requests here. Pattern mirrors
/// <see cref="FhirInsurancePlanController"/> byte-for-byte modulo the
/// route. Tenant scoping per BP 5.8 — see <c>TenantMiddleware</c>.
///
/// <para>
/// Endpoint resources are sourced from <see cref="BenefitPlan.Documents"/>.
/// Their FHIR id (Decision 2) is the underlying
/// <see cref="PlanDocumentReference.Id"/> verbatim. The search surface
/// honors <c>?_id</c>, <c>?status</c>, and <c>?connection-type</c>.
/// <c>?organization=</c> is deferred — Endpoint is currently only
/// referenced by InsurancePlan (no Organization link).
/// </para>
/// </summary>
[ApiController]
[Route("fhir")]
public class FhirEndpointController : ControllerBase
{
    private const string FhirContentType = "application/fhir+json";
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private readonly IBenefitPlanRepository _repository;
    private readonly IFhirEndpointProjector _projector;
    private readonly ILogger<FhirEndpointController> _logger;

    public FhirEndpointController(
        IBenefitPlanRepository repository,
        IFhirEndpointProjector projector,
        ILogger<FhirEndpointController> logger)
    {
        _repository = repository;
        _projector = projector;
        _logger = logger;
    }

    private string TenantId
        => HttpContext.GetTenantId() ?? throw new InvalidOperationException("Tenant context missing");

    /// <summary>
    /// FHIR Endpoint read by id. The path segment is the
    /// <see cref="PlanDocumentReference.Id"/> (Decision 2). Tenant scoping
    /// enforced — wrong-tenant lookups return 404 rather than 200 with an
    /// empty payload.
    /// </summary>
    [HttpGet("Endpoint/{id}")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> ReadEndpoint(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return FhirOperationOutcome(400, "invalid", "Endpoint id is required.");
        }

        var match = await FindEndpointAsync(id, ct);
        if (match is null)
        {
            return FhirOperationOutcome(404, "not-found", $"Endpoint/{id} not found.");
        }

        var projected = _projector.Project(match.Value.Plan, match.Value.Document);
        if (projected is null)
        {
            // Plan exists and document was found but the document is not
            // projectable — non-Published parent or internal-reference
            // location. Both surface as 404 to consumers.
            return FhirOperationOutcome(404, "not-found", $"Endpoint/{id} not found.");
        }

        return new ContentResult
        {
            ContentType = FhirContentType,
            Content = projected.ToJsonString(),
            StatusCode = 200,
        };
    }

    /// <summary>
    /// FHIR Endpoint search. Honors <c>_id</c>, <c>status</c>, and
    /// <c>connection-type</c>. <c>organization=</c> is deferred (no
    /// Organization→Endpoint link today). Pagination follows the BP 5.8
    /// InsurancePlan pattern.
    /// </summary>
    [HttpGet("Endpoint")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> SearchEndpoints(
        [FromQuery(Name = "_id")] string? _id,
        [FromQuery] string? status,
        [FromQuery(Name = "connection-type")] string? connectionType,
        [FromQuery] int _count = DefaultPageSize,
        [FromQuery] int _page = 1,
        CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(_count, 1, MaxPageSize);
        var page = Math.Max(1, _page);

        // _id is a token parameter. Bare value or system|value where the
        // system is empty — there is no canonical CHO Endpoint identifier
        // system because Endpoint.id is the only authority (Decision 2).
        if (!string.IsNullOrEmpty(_id))
        {
            var resolvedId = ParseTokenValue(_id);
            if (string.IsNullOrEmpty(resolvedId))
            {
                return BuildBundle(Array.Empty<JsonObject>());
            }
            var match = await FindEndpointAsync(resolvedId, ct);
            if (match is null) return BuildBundle(Array.Empty<JsonObject>());

            var projected = _projector.Project(match.Value.Plan, match.Value.Document);
            return BuildBundle(projected is null
                ? Array.Empty<JsonObject>()
                : new[] { projected });
        }

        // connection-type filter — only "static-document" matches today
        // (Decision 1 — CHO publishes one code). Any other value yields
        // a no-match bundle without scanning, FHIR token semantics.
        if (!string.IsNullOrEmpty(connectionType))
        {
            var resolvedConn = ParseTokenValue(connectionType);
            if (!string.Equals(resolvedConn,
                    ChoBenefitPlanFhirUrls.EndpointConnectionTypeStaticDocument,
                    StringComparison.OrdinalIgnoreCase))
            {
                return BuildBundle(Array.Empty<JsonObject>());
            }
        }

        // status is also a FHIR token (system|value | bare value). Run it
        // through ParseTokenValue so a system|value pair with an unknown
        // system yields no-match semantics consistent with _id and
        // connection-type. Copilot review BP 5.9.
        string? resolvedStatus = null;
        if (!string.IsNullOrEmpty(status))
        {
            resolvedStatus = ParseTokenValue(status);
            if (string.IsNullOrEmpty(resolvedStatus))
            {
                return BuildBundle(Array.Empty<JsonObject>());
            }
        }

        List<JsonObject> matches;
        try
        {
            matches = await CollectAllEndpointsAsync(resolvedStatus, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return FhirOperationOutcome(500, "exception", "Endpoint search failed.");
        }

        var pageItems = matches
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return BuildBundle(pageItems);
    }

    // ── helpers ────────────────────────────────────────────────────────

    private const int RepoScanLimit = 50;
    private const int RepoChunkSize = 200;

    /// <summary>
    /// Locate a (plan, document) pair whose document id matches the
    /// requested endpoint id within the caller's tenant. The repository
    /// has no document-id index today; we collect head-Published versions
    /// page-by-page and scan their <c>Documents</c> arrays.
    /// </summary>
    private async Task<(BenefitPlan Plan, PlanDocumentReference Document)?> FindEndpointAsync(
        string endpointId, CancellationToken ct)
    {
        await foreach (var plan in EnumerateHeadPublishedPlansAsync(ct))
        {
            if (plan.Documents is null || plan.Documents.Count == 0) continue;

            var doc = plan.Documents.FirstOrDefault(d =>
                d is not null && string.Equals(d.Id, endpointId, StringComparison.Ordinal));
            if (doc is not null) return (plan, doc);
        }
        return null;
    }

    private async Task<List<JsonObject>> CollectAllEndpointsAsync(
        string? statusFilter, CancellationToken ct)
    {
        // First collect (plan, document) pairs; sort across plans by the
        // canonical Decision 8 key BEFORE projecting + paging so paging is
        // deterministic regardless of repository iteration order. Copilot
        // review BP 5.9 — Decision 8 ordering must apply to the bundle, not
        // just to the per-plan slice.
        var pairs = new List<(BenefitPlan Plan, PlanDocumentReference Document)>();
        await foreach (var plan in EnumerateHeadPublishedPlansAsync(ct))
        {
            if (plan.Documents is null || plan.Documents.Count == 0) continue;

            foreach (var doc in _projector.OrderedProjectableDocuments(plan))
            {
                pairs.Add((plan, doc));
            }
        }

        var sortedPairs = pairs
            .OrderBy(p => FhirEndpointProjector.DocTypeOrdinal(p.Document.DocType))
            .ThenByDescending(p => p.Document.EffectiveDate ?? DateTime.MinValue)
            .ThenBy(p => p.Document.Id, StringComparer.Ordinal);

        var endpoints = new List<JsonObject>();
        foreach (var (plan, doc) in sortedPairs)
        {
            var projected = _projector.Project(plan, doc);
            if (projected is null) continue;

            if (!string.IsNullOrEmpty(statusFilter)
                && !string.Equals(
                    projected["status"]?.GetValue<string>(),
                    statusFilter,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            endpoints.Add(projected);
        }
        return endpoints;
    }


    private async IAsyncEnumerable<BenefitPlan> EnumerateHeadPublishedPlansAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var seenHead = new Dictionary<string, BenefitPlan>(StringComparer.Ordinal);

        for (var repoPage = 1; repoPage <= RepoScanLimit; repoPage++)
        {
            ct.ThrowIfCancellationRequested();

            IEnumerable<BenefitPlan> chunk;
            try
            {
                chunk = await _repository.SearchAsync(
                    tenantId: TenantId,
                    lineOfBusiness: null,
                    planType: null,
                    metalLevel: null,
                    page: repoPage,
                    pageSize: RepoChunkSize);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Endpoint search failed for tenant {TenantId} (repo page {RepoPage})",
                    SanitizeForLog(TenantId), repoPage);
                throw;
            }

            var chunkList = chunk as IList<BenefitPlan> ?? chunk.ToList();
            if (chunkList.Count == 0) break;

            foreach (var plan in chunkList)
            {
                if (plan.VersionState != PlanVersionState.Published) continue;
                if (plan.EffectiveDate > now) continue;

                if (seenHead.TryGetValue(plan.PlanId, out var existing) &&
                    existing.VersionNumber >= plan.VersionNumber)
                {
                    continue;
                }
                seenHead[plan.PlanId] = plan;
            }

            if (chunkList.Count < RepoChunkSize) break;
        }

        // Dictionary value enumeration order is not contractually
        // guaranteed; emit in a stable order so callers (FindEndpointAsync
        // / CollectAllEndpointsAsync) see the same plan sequence across
        // runs. Sort by PlanName then PlanId, matching the BP 5.8
        // InsurancePlan search ordering. Copilot review BP 5.9.
        var ordered = seenHead.Values
            .OrderBy(p => p.PlanName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.PlanId, StringComparer.Ordinal);
        foreach (var plan in ordered)
        {
            yield return plan;
        }
    }

    private IActionResult BuildBundle(IReadOnlyCollection<JsonObject> resources)
    {
        var entries = new JsonArray();
        foreach (var resource in resources)
        {
            entries.Add(new JsonObject
            {
                ["resource"] = resource,
                ["search"] = new JsonObject { ["mode"] = "match" },
            });
        }

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
                }
            },
        };
        return new ContentResult
        {
            ContentType = FhirContentType,
            Content = outcome.ToJsonString(),
            StatusCode = status,
        };
    }

    /// <summary>
    /// Parse a FHIR token-shaped query parameter. Returns the token's
    /// value component, or null when the input is malformed / a
    /// system|value pair whose system we don't recognize. The Endpoint
    /// surface has no canonical identifier system today (Decision 2 —
    /// the FHIR id IS the identifier), so any non-empty system part
    /// rejects.
    /// </summary>
    private static string? ParseTokenValue(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        var pipe = token.IndexOf('|');
        if (pipe < 0) return token;

        var system = token[..pipe];
        var value  = token[(pipe + 1)..];
        if (string.IsNullOrEmpty(system)) return value;
        return null;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
