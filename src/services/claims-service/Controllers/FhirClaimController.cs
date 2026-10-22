using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using ClaimsService.Audit;
using ClaimsService.Fhir;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// FHIR R4 Claim read and search — the claim <em>as billed</em>, alongside the
/// adjudicated <see cref="FhirExplanationOfBenefitController"/> view of the
/// same record. claims-service owns both projections; fhir-service parses FHIR
/// search syntax and calls this endpoint with resolved, structured filters, as
/// it does for member-service's Patient and coverage-service's Coverage
/// (ADR 018). This endpoint never parses FHIR search syntax.
///
/// <para>
/// FHIR resource id is the chain-stable <see cref="Claim.ClaimVersionId"/>, the
/// same id the EOB projection uses, so <c>Claim/{id}</c> and
/// <c>ExplanationOfBenefit/{id}</c> name one claim. Reads resolve the chain
/// head, and searches return current versions only.
/// </para>
///
/// <para>
/// Tenancy: the tenant comes from claims-service's tenant middleware and every
/// query filters on it; a missing tenant is a FHIR <c>OperationOutcome</c> 400
/// rather than a 500.
/// </para>
/// </summary>
[ApiController]
[Route("fhir")]
public sealed class FhirClaimController : ControllerBase
{
    private const string FhirContentType = "application/fhir+json";

    private readonly IClaimRepository _repository;
    private readonly IClaimFhirSearchRepository _search;
    private readonly IClaimFhirProjector _projector;
    private readonly ILogger<FhirClaimController> _logger;

    public FhirClaimController(
        IClaimRepository repository,
        IClaimFhirSearchRepository search,
        IClaimFhirProjector projector,
        ILogger<FhirClaimController> logger)
    {
        _repository = repository;
        _search = search;
        _projector = projector;
        _logger = logger;
    }

    /// <summary>
    /// FHIR R4 Claim projection of the head version of the chain identified by
    /// <paramref name="id"/>.
    /// </summary>
    [HttpGet("Claim/{id}")]
    [AuditClaimAccess(ClaimAccessActions.View, ClaimIdRouteValue = "id")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> ReadClaim(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            return FhirOperationOutcome(400, "invalid", "Claim id is required.");

        var tenantId = TryGetTenantId();
        if (string.IsNullOrEmpty(tenantId))
            return FhirOperationOutcome(400, "invalid", "Tenant context missing on request.");

        Claim? claim;
        try
        {
            claim = await _repository.GetLatestVersionAsync(id, DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claim read failed for tenant {TenantId}", LogSanitizer.Sanitize(tenantId));
            return FhirOperationOutcome(500, "exception", "Claim read failed.");
        }

        if (claim is null)
            return FhirOperationOutcome(404, "not-found", "Claim not found.");

        return new ContentResult
        {
            ContentType = FhirContentType,
            Content = _projector.Project(claim).ToJsonString(),
            StatusCode = 200,
        };
    }

    /// <summary>
    /// FHIR R4 Claim projections of the current claim versions matching
    /// structured filters. Results are ordered by submission, newest first, and
    /// the total counts every match so the caller can page.
    /// </summary>
    [HttpGet("Claim")]
    [AuditClaimAccess(ClaimAccessActions.Search, MemberQueryParameters = ["memberId"])]
    [ProducesResponseType(typeof(ClaimFhirSearchResponse), 200)]
    public async Task<IActionResult> SearchClaims(
        [FromQuery] string? claimVersionId = null,
        [FromQuery] string? memberId = null,
        [FromQuery] DateTime? createdFrom = null,
        [FromQuery] DateTime? createdTo = null,
        [FromQuery] string? status = null,
        [FromQuery] ClaimType? claimType = null,
        [FromQuery] DateTime? lastUpdatedFrom = null,
        [FromQuery] DateTime? lastUpdatedTo = null,
        [FromQuery][Range(0, ClaimFhirSearchQuery.MaxOffset)] int offset = 0,
        [FromQuery][Range(0, ClaimFhirSearchQuery.MaxLimit)] int limit = 20,
        CancellationToken ct = default)
    {
        var tenantId = TryGetTenantId();
        if (string.IsNullOrEmpty(tenantId))
            return FhirOperationOutcome(400, "invalid", "Tenant context missing on request.");

        // A status the projection never emits is a caller error, not an empty
        // result: fhir-service validates the code set before it gets here, so
        // reaching this means the two disagree and silence would hide it.
        if (status is not null && !FhirClaimStatusCodes.IsEmitted(status.Trim().ToLowerInvariant()))
        {
            return FhirOperationOutcome(400, "invalid",
                $"status must be {FhirClaimStatusCodes.Active} or {FhirClaimStatusCodes.Cancelled}.");
        }

        var query = new ClaimFhirSearchQuery
        {
            ClaimVersionId = claimVersionId,
            MemberId = memberId,
            CreatedFrom = createdFrom,
            CreatedTo = createdTo,
            Status = status,
            ClaimType = claimType,
            LastUpdatedFrom = lastUpdatedFrom,
            LastUpdatedTo = lastUpdatedTo,
            Offset = offset,
            Limit = limit,
        };

        try
        {
            var (claims, total) = await _search.SearchAsync(tenantId, query, ct);
            var items = claims
                .Select(c => new ClaimFhirSearchEntry(
                    string.IsNullOrEmpty(c.ClaimVersionId) ? c.Id : c.ClaimVersionId,
                    c.MemberId,
                    _projector.Project(c)))
                .ToList();
            return Ok(new ClaimFhirSearchResponse(total, items));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claim search failed for tenant {TenantId}", LogSanitizer.Sanitize(tenantId));
            return FhirOperationOutcome(500, "exception", "Claim search failed.");
        }
    }

    private string? TryGetTenantId() => HttpContext.Items["TenantId"]?.ToString();

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
}
