using System.Text.Json.Nodes;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// FHIR R4 InsurancePlan read + search endpoint (capability BP 5.8).
/// benefit-plan-service is the canonical authority on the projection;
/// fhir-service proxies <c>/fhir/r4/InsurancePlan/*</c> requests here so
/// CHO retains a single FHIR façade for external consumers while each
/// domain service owns its own projection (mirrors provider-service's
/// <c>FhirPractitionerController</c> / <c>FhirOrganizationController</c>
/// for the Plan-Net Provider Directory bundle).
///
/// <para>
/// Tenant scoping per Decision 7: requests honor the existing
/// <see cref="Middleware.TenantMiddleware"/> mechanism. Authenticated /
/// header-scoped callers see their tenant's plans only. Public
/// CMS-0057-F unauthenticated access is a Phase 2 capability.
/// </para>
/// </summary>
[ApiController]
[Route("fhir")]
public class FhirInsurancePlanController : ControllerBase
{
    private const string FhirContentType = "application/fhir+json";
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private readonly IBenefitPlanRepository _repository;
    private readonly IFhirInsurancePlanProjector _projector;
    private readonly IOrganizationLookupClient _organizationLookup;
    private readonly IAcaLimitsProvider _acaLimits;
    private readonly IPlanYearResolver _planYearResolver;
    private readonly ILogger<FhirInsurancePlanController> _logger;

    public FhirInsurancePlanController(
        IBenefitPlanRepository repository,
        IFhirInsurancePlanProjector projector,
        IOrganizationLookupClient organizationLookup,
        IAcaLimitsProvider acaLimits,
        IPlanYearResolver planYearResolver,
        ILogger<FhirInsurancePlanController> logger)
    {
        _repository = repository;
        _projector = projector;
        _organizationLookup = organizationLookup;
        _acaLimits = acaLimits;
        _planYearResolver = planYearResolver;
        _logger = logger;
    }

    private string TenantId
        => HttpContext.GetTenantId() ?? throw new InvalidOperationException("Tenant context missing");

    /// <summary>
    /// FHIR InsurancePlan read by PlanId. The path segment is the FHIR
    /// resource <c>id</c>, which capability BP 5.8 maps to
    /// <see cref="BenefitPlan.PlanId"/> per Decision 6 (the
    /// operator-supplied human-meaningful identifier consumers see on
    /// member ID cards and SBC documents). Tenant scoping disambiguates
    /// the rare case where two tenants happen to use the same value.
    /// </summary>
    [HttpGet("InsurancePlan/{id}")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> ReadInsurancePlan(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return FhirOperationOutcome(400, "invalid", "InsurancePlan id is required.");
        }

        BenefitPlan? plan;
        try
        {
            plan = await _repository.GetByPlanIdAsync(id, TenantId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled (client disconnect, server abort).
            // Propagate rather than turn it into a logged 500 — the
            // pipeline maps cancellation to its standard 499/aborted
            // shape and avoids polluting metrics with phantom errors.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InsurancePlan read failed for PlanId {PlanId}", SanitizeForLog(id));
            return FhirOperationOutcome(500, "exception", "InsurancePlan read failed.");
        }

        if (plan is null)
        {
            return FhirOperationOutcome(404, "not-found", $"InsurancePlan/{id} not found.");
        }

        var projected = await ProjectAsync(plan, ct);
        if (projected is null)
        {
            // Plan exists but no Active version satisfies the effective
            // window. Mirrors Provider 5.7's stance — a non-Active
            // version of a resource has no public FHIR projection.
            return FhirOperationOutcome(404, "not-found", $"InsurancePlan/{id} not found.");
        }

        return new ContentResult
        {
            ContentType = FhirContentType,
            Content = projected.ToJsonString(),
            StatusCode = 200,
        };
    }

    /// <summary>
    /// FHIR InsurancePlan search. Honors a deliberately small subset of
    /// FHIR search parameters: <c>identifier</c>, <c>name</c>,
    /// <c>status</c>, <c>_count</c>, <c>_page</c>. Other Plan-Net search
    /// parameters (<c>type</c>, <c>owned-by</c>, <c>administered-by</c>,
    /// <c>address</c>) are deferred — CHO's search backend doesn't index
    /// them today.
    /// </summary>
    [HttpGet("InsurancePlan")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> SearchInsurancePlans(
        [FromQuery] string? identifier,
        [FromQuery] string? name,
        [FromQuery] string? status,
        [FromQuery] int _count = DefaultPageSize,
        [FromQuery] int _page = 1,
        CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(_count, 1, MaxPageSize);
        var page = Math.Max(1, _page);

        // identifier is a token parameter. We accept either bare value
        // or system|value where system is the CHO PlanIdSystem. If the
        // caller supplied identifier= but the system is something we
        // don't know, FHIR token semantics require us to return zero
        // matches rather than ignore the filter — the empty bundle is
        // a no-match response, not an error.
        if (!string.IsNullOrEmpty(identifier))
        {
            var resolvedPlanId = ParsePlanIdentifier(identifier);
            if (string.IsNullOrEmpty(resolvedPlanId))
            {
                return await BuildBundleResponseAsync(Array.Empty<BenefitPlan>(), ct);
            }
            var plan = await _repository.GetByPlanIdAsync(resolvedPlanId, TenantId);
            return await BuildBundleResponseAsync(
                plan is null ? Array.Empty<BenefitPlan>() : new[] { plan },
                ct);
        }

        // Pagination correctness — `IBenefitPlanRepository.SearchAsync`
        // returns every version row (Draft / Published / Superseded)
        // ordered by PlanName, with no head-Published filter. Naively
        // paginating that and projecting in-place produces short pages
        // (the projector returns null for non-Published) and lets a
        // single PlanId surface multiple versions in the same response.
        //
        // Until the repository grows a head-Published search seam,
        // collect across repo pages, dedupe to the head Published
        // version per PlanId, apply the name / status / effective-window
        // filters, then page in-memory. Capped by `RepoScanLimit` so
        // a degenerate large-tenant scan can't run unbounded.
        List<BenefitPlan> matches;
        try
        {
            matches = await CollectFilteredHeadVersionsAsync(name, status, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // CollectFilteredHeadVersionsAsync already logged the
            // underlying repo failure; emit a FHIR-compliant error
            // surface here.
            return FhirOperationOutcome(500, "exception", "InsurancePlan search failed.");
        }

        var pageItems = matches
            .Skip((page - 1) * pageSize)
            .Take(pageSize);

        return await BuildBundleResponseAsync(pageItems, ct);
    }

    /// <summary>
    /// Hard cap on how many repo pages a single InsurancePlan search
    /// will scan. At <c>RepoChunkSize=200</c> rows per page that's
    /// 10,000 versions — well above any tenant's authored plan-version
    /// count today. If a real workload pushes against this, the right
    /// move is a head-Published seam on the repo, not raising the cap.
    /// </summary>
    private const int RepoScanLimit = 50;
    private const int RepoChunkSize = 200;

    private async Task<List<BenefitPlan>> CollectFilteredHeadVersionsAsync(
        string? nameFilter, string? statusFilter, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // PlanId → head Published version, keyed in iteration order so
        // PlanName sort from the repo is preserved.
        var headByPlanId = new Dictionary<string, BenefitPlan>(StringComparer.Ordinal);

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
                    "InsurancePlan search failed for tenant {TenantId} (repo page {RepoPage})",
                    SanitizeForLog(TenantId), repoPage);
                throw;
            }

            var chunkList = chunk as IList<BenefitPlan> ?? chunk.ToList();
            if (chunkList.Count == 0) break;

            foreach (var plan in chunkList)
            {
                if (plan.VersionState != PlanVersionState.Published) continue;
                if (plan.EffectiveDate > now) continue;

                // Dedupe to the head Published version per PlanId. Repo
                // ordering is by PlanName not VersionNumber, so we keep
                // the row with the highest VersionNumber we've seen.
                if (headByPlanId.TryGetValue(plan.PlanId, out var existing) &&
                    existing.VersionNumber >= plan.VersionNumber)
                {
                    continue;
                }
                headByPlanId[plan.PlanId] = plan;
            }

            if (chunkList.Count < RepoChunkSize) break; // repo exhausted
        }

        IEnumerable<BenefitPlan> filtered = headByPlanId.Values;

        if (!string.IsNullOrEmpty(nameFilter))
        {
            filtered = filtered.Where(p =>
                !string.IsNullOrEmpty(p.PlanName) &&
                p.PlanName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(statusFilter))
        {
            filtered = statusFilter.ToLowerInvariant() switch
            {
                "active" => filtered.Where(p => !p.TerminationDate.HasValue || p.TerminationDate.Value >= now),
                "retired" => filtered.Where(p => p.TerminationDate.HasValue && p.TerminationDate.Value < now),
                _ => filtered,
            };
        }

        return filtered.OrderBy(p => p.PlanName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── helpers ────────────────────────────────────────────────────────

    private async Task<JsonObject?> ProjectAsync(BenefitPlan plan, CancellationToken ct)
    {
        var networks = await ResolveNetworksAsync(plan, ct);
        var acaLimits = ResolveAcaLimits(plan);
        return _projector.Project(plan, networks, acaLimits);
    }

    /// <summary>
    /// Pre-fetch every distinct <c>NetworkId</c> from the plan's
    /// NetworkTiers via <see cref="IOrganizationLookupClient"/> and pass
    /// the resulting list to the projector. Keeps the projector pure
    /// (no DI / I/O) and lets the controller cache or coalesce lookups
    /// in the future without changing the projector contract.
    /// </summary>
    private async Task<IReadOnlyList<OrganizationLookupResult>?> ResolveNetworksAsync(
        BenefitPlan plan, CancellationToken ct)
    {
        if (plan.NetworkTiers is null || plan.NetworkTiers.Count == 0) return null;

        var distinctIds = plan.NetworkTiers
            .Where(t => !string.IsNullOrWhiteSpace(t.NetworkId))
            .Select(t => t.NetworkId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinctIds.Count == 0) return null;

        var resolved = new List<OrganizationLookupResult>(distinctIds.Count);
        foreach (var id in distinctIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var org = await _organizationLookup.GetOrganizationAsync(id, ct);
                if (org is not null) resolved.Add(org);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancelled — don't keep enriching networks the
                // response will never reach. Propagate so the pipeline
                // returns its standard cancellation shape.
                throw;
            }
            catch (Exception ex)
            {
                // Lookup failures are non-fatal — the projection is
                // still valid without display-text enrichment. Log and
                // continue so an unreachable provider-service doesn't
                // gate the InsurancePlan surface.
                _logger.LogWarning(ex,
                    "Organization lookup failed for network {NetworkId}; projecting without display enrichment",
                    SanitizeForLog(id));
            }
        }

        return resolved;
    }

    private AcaLimits? ResolveAcaLimits(BenefitPlan plan)
    {
        if (plan.FamilyAccumulatorModel != FamilyAccumulatorModel.Aggregate) return null;
        if (!AcaCapEnforcementPolicy.IsEnforced(plan)) return null;

        var planYear = _planYearResolver.Resolve(plan);
        return _acaLimits.GetForPlanYear(planYear);
    }

    private async Task<IActionResult> BuildBundleResponseAsync(
        IEnumerable<BenefitPlan> plans, CancellationToken ct)
    {
        var entries = new JsonArray();
        foreach (var plan in plans)
        {
            var projected = await ProjectAsync(plan, ct);
            if (projected is null) continue;
            entries.Add(new JsonObject
            {
                ["resource"] = projected,
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

    private static string? ParsePlanIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return null;

        var pipe = identifier.IndexOf('|');
        if (pipe >= 0)
        {
            var system = identifier[..pipe];
            var value = identifier[(pipe + 1)..];
            if (string.IsNullOrEmpty(system)
                || string.Equals(system, ChoBenefitPlanFhirUrls.PlanIdSystem, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
            // Unknown system — caller intent is ambiguous; reject is the
            // FHIR-correct response but we treat as no-match here so the
            // search returns an empty bundle rather than a 400 (consistent
            // with the parameter being optional).
            return null;
        }
        return identifier;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
