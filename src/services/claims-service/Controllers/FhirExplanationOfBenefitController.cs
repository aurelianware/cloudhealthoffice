using System.Text.Json.Nodes;
using ClaimsService.Fhir;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using Microsoft.AspNetCore.Mvc;

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

    private readonly IClaimRepository _repository;
    private readonly IExplanationOfBenefitProjector _projector;
    private readonly IClaimDiagnosisMetadataEnricher _diagnosisMetadataEnricher;
    private readonly ILogger<FhirExplanationOfBenefitController> _logger;

    public FhirExplanationOfBenefitController(
        IClaimRepository repository,
        IExplanationOfBenefitProjector projector,
        IClaimDiagnosisMetadataEnricher diagnosisMetadataEnricher,
        ILogger<FhirExplanationOfBenefitController> logger)
    {
        _repository = repository;
        _projector = projector;
        _diagnosisMetadataEnricher = diagnosisMetadataEnricher;
        _logger = logger;
    }

    /// <summary>
    /// FHIR ExplanationOfBenefit read by claim-version id. Returns the
    /// head version of the chain identified by <paramref name="id"/> as
    /// of <c>DateTime.UtcNow</c> so consumers see the latest adjudication
    /// state. Per-version (<c>_history</c>) reads are Phase 2.
    /// </summary>
    [HttpGet("ExplanationOfBenefit/{id}")]
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
                SanitizeForLog(id));
            return FhirOperationOutcome(500, "exception",
                "ExplanationOfBenefit read failed.");
        }

        if (claim is null)
        {
            return FhirOperationOutcome(404, "not-found",
                $"ExplanationOfBenefit/{id} not found.");
        }

        await _diagnosisMetadataEnricher.EnrichAsync(claim, ct);
        var projected = _projector.Project(claim);
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
    /// Other CARIN BB / patient-access search parameters
    /// (<c>created</c>, <c>provider</c>, <c>status</c>, <c>type</c>,
    /// <c>identifier</c>, <c>_lastUpdated</c>, <c>_include</c>,
    /// <c>_revinclude</c>) are deferred to Phase 2 with a repository
    /// search-seam expansion.
    /// </summary>
    [HttpGet("ExplanationOfBenefit")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> SearchEobs(
        [FromQuery] string? patient,
        [FromQuery(Name = "_id")] string? id,
        [FromQuery(Name = "_count")] int count = DefaultPageSize,
        [FromQuery(Name = "_page")] int page = 1,
        CancellationToken ct = default)
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
                SanitizeForLog(tenantId));
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
                ["resource"] = _projector.Project(claim),
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

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}
