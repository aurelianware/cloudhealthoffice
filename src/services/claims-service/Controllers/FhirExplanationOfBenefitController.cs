using ClaimsService.Audit;
using System.Text.Json.Nodes;
using ClaimsService.Fhir;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using Microsoft.AspNetCore.Mvc;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Controllers;

/// <summary>
/// FHIR R4 ExplanationOfBenefit read + search endpoint (capability 5.11).
/// claims-service is the canonical authority on the projection;
/// fhir-service proxies <c>/fhir/r4/ExplanationOfBenefit/*</c> requests
/// here so CHO retains a single FHIR façade for external consumers while
/// each domain service owns its own projection (mirrors the BP 5.8
/// FhirInsurancePlanController and Provider 5.7-5.9 controllers for the
/// rest of the Plan-Net Provider Directory bundle).
///
/// <para>
/// FHIR resource id is the chain-stable <see cref="Claim.ClaimVersionId"/>;
/// reads resolve via <see cref="IClaimRepository.GetLatestVersionAsync"/>
/// so consumers see the head version of an adjustment chain through one
/// stable id (Decision 11). Per-version reads (<c>_history</c>) are
/// deferred to Phase 2 alongside the adjustment workflow (capability
/// 5.12).
/// </para>
///
/// <para>
/// Tenant scoping (Decision 6): the existing claims-service tenant
/// middleware populates <c>HttpContext.Items["TenantId"]</c>; missing
/// tenant returns a FHIR <c>OperationOutcome</c> 400 rather than a 500.
/// Public CMS-0057-F unauthenticated access is a Phase 2 capability.
/// </para>
/// </summary>
[ApiController]
[Route("fhir")]
public class FhirExplanationOfBenefitController : ControllerBase
{
    private const string FhirContentType = "application/fhir+json";
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    /// <summary>
    /// The route segment that selects the Provider Access payload: the same
    /// claims as CARIN Blue Button <b>non-financial</b> ExplanationOfBenefits.
    /// </summary>
    public const string ProviderAccessSegment = "ProviderAccess";

    private readonly IClaimRepository _repository;
    private readonly IExplanationOfBenefitProjector _projector;
    private readonly ICarinNonFinancialEobProjector _nonFinancialProjector;
    private readonly IClaimDiagnosisMetadataEnricher _diagnosisMetadataEnricher;
    private readonly IClaimFhirSearchRepository? _search;
    private readonly ILogger<FhirExplanationOfBenefitController> _logger;

    public FhirExplanationOfBenefitController(
        IClaimRepository repository,
        IExplanationOfBenefitProjector projector,
        ICarinNonFinancialEobProjector nonFinancialProjector,
        IClaimDiagnosisMetadataEnricher diagnosisMetadataEnricher,
        ILogger<FhirExplanationOfBenefitController> logger,
        IClaimFhirSearchRepository? search = null)
    {
        _repository = repository;
        _search = search;
        _projector = projector;
        _nonFinancialProjector = nonFinancialProjector;
        _diagnosisMetadataEnricher = diagnosisMetadataEnricher;
        _logger = logger;
    }

    /// <summary>
    /// Which payload this request asks for. Patient Access gets the adjudicated
    /// EOB; Provider Access gets the CARIN BB non-financial one, which carries
    /// the same encounter without the plan's financial terms.
    ///
    /// <para>
    /// The two share one pair of actions on purpose. Everything else about them
    /// is identical — the chain-head read, the <c>_id</c>-versus-<c>patient</c>
    /// rule that stops a bound token tunnelling to another member, the paging —
    /// and a second controller would be a second chance to get one of those
    /// wrong on only one of the paths. fhir-service applies the SMART scope,
    /// the attribution check and the member confinement before either is
    /// reached; this service decides nothing but tenancy and the projection.
    /// </para>
    /// </summary>
    private bool IsProviderAccess
        => Request.Path.Value?.Contains("/" + ProviderAccessSegment + "/", StringComparison.OrdinalIgnoreCase)
           == true;

    private JsonObject Project(Claim claim)
        => IsProviderAccess ? _nonFinancialProjector.Project(claim) : _projector.Project(claim);

    /// <summary>
    /// FHIR ExplanationOfBenefit read by claim-version id. Returns the
    /// head version of the chain identified by <paramref name="id"/> as
    /// of <c>DateTime.UtcNow</c> so consumers see the latest adjudication
    /// state. Per-version (<c>_history</c>) reads are Phase 2.
    /// </summary>
    [HttpGet("ExplanationOfBenefit/{id}")]
    [HttpGet(ProviderAccessSegment + "/ExplanationOfBenefit/{id}")]
    [AuditClaimAccess(ClaimAccessActions.Eob, ClaimIdRouteValue = "id")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> ReadEob(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return FhirOperationOutcome(400, "invalid",
                "ExplanationOfBenefit id is required.");
        }

        var tenantId = TryGetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return FhirOperationOutcome(400, "invalid",
                "Tenant context missing on request.");
        }

        Claim? claim;
        try
        {
            // GetLatestVersionAsync resolves the chain head as of "now".
            // Legacy rows (predating the version chain) hydrate so
            // ClaimVersionId == Id, which means callers can use either
            // identifier and get the same row back.
            claim = await _repository.GetLatestVersionAsync(id, DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled (client disconnect, server abort).
            // Propagate rather than turning into a logged 500 — the
            // pipeline maps cancellation to its standard 499/aborted
            // shape and avoids polluting metrics with phantom errors.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ExplanationOfBenefit read failed for id {Id}",
                LogSanitizer.Sanitize(id));
            return FhirOperationOutcome(500, "exception",
                "ExplanationOfBenefit read failed.");
        }

        if (claim is null)
        {
            return FhirOperationOutcome(404, "not-found",
                $"ExplanationOfBenefit/{id} not found.");
        }

        await _diagnosisMetadataEnricher.EnrichAsync(claim, ct);
        var projected = Project(claim);
        return new ContentResult
        {
            ContentType = FhirContentType,
            Content = projected.ToJsonString(),
            StatusCode = 200,
        };
    }

    /// <summary>
    /// FHIR ExplanationOfBenefit search. Phase 1 honors a deliberately
    /// small parameter set (Decision 7):
    /// <list type="bullet">
    ///   <item><c>patient</c> (member id) — required for safety unless
    ///         <c>_id</c> is supplied. Matches the upstream SMART scope
    ///         enforcement: a patient-bound token cannot read EOBs that
    ///         don't belong to the bound patient.</item>
    ///   <item><c>_id</c> — direct lookup by FHIR resource id (treated as
    ///         <see cref="Claim.ClaimVersionId"/>).</item>
    ///   <item><c>_count</c> — page size, default 50, clamped to 200.</item>
    ///   <item><c>_page</c> — 1-based pagination cursor.</item>
    /// </list>
    /// The CARIN BB search parameters arrive as structured filters that
    /// fhir-service has already parsed from FHIR syntax (ADR 018), as they do
    /// for <see cref="FhirClaimController"/>: <c>claimType</c> (repeatable,
    /// any of), <c>claimNumber</c>, <c>serviceFrom</c>/<c>serviceTo</c>,
    /// <c>serviceStartFrom</c>/<c>serviceStartTo</c>,
    /// <c>createdFrom</c>/<c>createdTo</c>, <c>lastUpdatedFrom</c>/<c>lastUpdatedTo</c>
    /// and <c>status</c>. A search with any of them goes through
    /// <see cref="IClaimFhirSearchRepository"/>, which applies them in the
    /// store; a search with none is the member listing it has always been.
    /// </summary>
    [HttpGet("ExplanationOfBenefit")]
    [HttpGet(ProviderAccessSegment + "/ExplanationOfBenefit")]
    [AuditClaimAccess(ClaimAccessActions.Eob)]
    [Produces(FhirContentType)]
    public async Task<IActionResult> SearchEobs(
        [FromQuery] string? patient,
        [FromQuery(Name = "_id")] string? id,
        [FromQuery(Name = "_count")] int count = DefaultPageSize,
        [FromQuery(Name = "_page")] int page = 1,
        // Before the filters so a caller passing the paging arguments and the
        // token positionally is not broken by them.
        CancellationToken ct = default,
        [FromQuery] ClaimType[]? claimType = null,
        [FromQuery] string? claimNumber = null,
        [FromQuery] DateTime? serviceFrom = null,
        [FromQuery] DateTime? serviceTo = null,
        [FromQuery] DateTime? serviceStartFrom = null,
        [FromQuery] DateTime? serviceStartTo = null,
        [FromQuery] DateTime? createdFrom = null,
        [FromQuery] DateTime? createdTo = null,
        [FromQuery] DateTime? lastUpdatedFrom = null,
        [FromQuery] DateTime? lastUpdatedTo = null,
        [FromQuery] string? status = null)
    {
        var tenantId = TryGetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return FhirOperationOutcome(400, "invalid",
                "Tenant context missing on request.");
        }

        if (string.IsNullOrEmpty(patient) && string.IsNullOrEmpty(id))
        {
            return FhirOperationOutcome(400, "invalid",
                "ExplanationOfBenefit search requires either the patient or _id search parameter.");
        }

        // Normalize FHIR-typed references — `patient=Patient/123` is
        // equivalent to `patient=123` per FHIR search semantics. Without
        // this strip the repository read and the _id-vs-patient compare
        // both miss when callers use the typed form.
        var normalizedPatient = StripPatientPrefix(patient);

        var pageSize = Math.Clamp(count, 1, MaxPageSize);
        var pageNumber = Math.Max(1, page);

        if (status is not null && !FhirClaimStatusCodes.IsEmitted(status.Trim().ToLowerInvariant()))
        {
            return FhirOperationOutcome(400, "invalid",
                $"status must be {FhirClaimStatusCodes.Active} or {FhirClaimStatusCodes.Cancelled}.");
        }

        var filtered = claimType is { Length: > 0 } || !string.IsNullOrWhiteSpace(claimNumber)
            || serviceFrom is not null || serviceTo is not null
            || serviceStartFrom is not null || serviceStartTo is not null
            || createdFrom is not null || createdTo is not null
            || lastUpdatedFrom is not null || lastUpdatedTo is not null
            || status is not null;

        if (filtered)
        {
            if (_search is null)
            {
                return FhirOperationOutcome(400, "not-supported",
                    "ExplanationOfBenefit search filters need the MongoDB claim store.");
            }

            var offset = (pageNumber - 1) * pageSize;
            if (offset > ClaimFhirSearchQuery.MaxOffset)
            {
                return FhirOperationOutcome(400, "invalid",
                    $"_page reaches past the {ClaimFhirSearchQuery.MaxOffset}th result.");
            }

            var query = new ClaimFhirSearchQuery
            {
                ClaimVersionId = id,
                MemberId = normalizedPatient,
                ClaimTypes = claimType,
                ClaimNumber = claimNumber,
                ServiceFrom = serviceFrom,
                ServiceTo = serviceTo,
                ServiceStartFrom = serviceStartFrom,
                ServiceStartTo = serviceStartTo,
                CreatedFrom = createdFrom,
                CreatedTo = createdTo,
                LastUpdatedFrom = lastUpdatedFrom,
                LastUpdatedTo = lastUpdatedTo,
                Status = status,
                Offset = offset,
                Limit = pageSize,
            };

            try
            {
                var (matches, total) = await _search.SearchAsync(tenantId, query, ct);
                await _diagnosisMetadataEnricher.EnrichAsync(matches, ct);
                return BuildBundleResponse(matches, (int)Math.Min(total, int.MaxValue));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ExplanationOfBenefit search failed for tenant {TenantId}",
                    LogSanitizer.Sanitize(tenantId));
                return FhirOperationOutcome(500, "exception",
                    "ExplanationOfBenefit search failed.");
            }
        }

        try
        {
            // _id takes precedence — direct lookup of a single chain head
            // by ClaimVersionId. FHIR semantics require the patient
            // parameter, when both are supplied, to match the looked-up
            // resource's patient; we enforce this so a patient-scoped
            // token can't tunnel through _id to read a different patient's
            // EOB.
            if (!string.IsNullOrEmpty(id))
            {
                var single = await _repository.GetLatestVersionAsync(id, DateTime.UtcNow);
                if (single is null)
                {
                    return BuildBundleResponse(Array.Empty<Claim>(), total: 0);
                }
                if (!string.IsNullOrEmpty(normalizedPatient) &&
                    !string.Equals(single.MemberId, normalizedPatient, StringComparison.Ordinal))
                {
                    // _id resolved but the resource doesn't belong to the
                    // requested patient. Returning an empty bundle rather
                    // than a 403 keeps SMART-bound clients from leaking
                    // existence-of-resource through the status code.
                    return BuildBundleResponse(Array.Empty<Claim>(), total: 0);
                }
                await _diagnosisMetadataEnricher.EnrichAsync(single, ct);
                return BuildBundleResponse(new[] { single }, total: 1);
            }

            var (items, totalCount) = await _repository.SearchForMemberAsync(
                memberId: normalizedPatient!,
                serviceDateFrom: null,
                serviceDateTo: null,
                status: null,
                providerNPI: null,
                claimType: null,
                amountMin: null,
                amountMax: null,
                page: pageNumber,
                pageSize: pageSize);

            await _diagnosisMetadataEnricher.EnrichAsync(items, ct);
            return BuildBundleResponse(items, totalCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ExplanationOfBenefit search failed for tenant {TenantId}",
                LogSanitizer.Sanitize(tenantId));
            return FhirOperationOutcome(500, "exception",
                "ExplanationOfBenefit search failed.");
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private IActionResult BuildBundleResponse(IEnumerable<Claim> claims, int total)
    {
        var entries = new JsonArray();
        foreach (var claim in claims)
        {
            entries.Add(new JsonObject
            {
                ["resource"] = Project(claim),
                ["search"] = new JsonObject { ["mode"] = "match" },
            });
        }

        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = total,
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

    private string TryGetTenantId() =>
        HttpContext?.Items["TenantId"]?.ToString() ?? string.Empty;

    /// <summary>
    /// FHIR search parameters that target a Reference type accept either
    /// a bare logical id or a typed reference (e.g. <c>Patient/123</c>).
    /// claims-service stores raw member ids, so strip the optional
    /// <c>Patient/</c> prefix before using the value as a memberId.
    /// </summary>
    private static string? StripPatientPrefix(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        const string prefix = "Patient/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }
}
