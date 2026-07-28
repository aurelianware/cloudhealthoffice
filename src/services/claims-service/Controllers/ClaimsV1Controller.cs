using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaimsService.Adapters;
using ClaimsService.Fhir;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// v1 claims API — the canonical surface for claim submission and
/// member-scoped search.
///
/// <para>
/// <b>POST</b> — capability 5.3 ships the canonical
/// <c>POST /api/v1/claims</c> submission endpoint. Accepts an
/// <see cref="AdapterClaim"/> (vendor-neutral DTO from 5.2),
/// orchestrates validation + adapter call + version-event emission
/// through <see cref="IClaimSubmissionService"/>, and returns the
/// created claim version. Legacy <c>POST /api/claims</c> is marked
/// <c>[Obsolete]</c> and routes through the same service so the
/// audit chain is continuous regardless of which surface a caller
/// picks.
/// </para>
///
/// <para>
/// <b>GET</b> — member-scoped search powering the portal Member
/// Details Claims tab. Reads through the tenant-routed
/// <see cref="IClaimAdapter"/> (5.2) and projects each
/// <see cref="AdapterClaim"/> onto a FHIR R4 ExplanationOfBenefit
/// resource via <see cref="IExplanationOfBenefitProjector"/>. The
/// response shape (<see cref="EobSearchResponse"/>) is unchanged
/// from the pre-5.3 repo-routed implementation — portal contract
/// preserved.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/claims")]
[Produces("application/json")]
public class ClaimsV1Controller : ControllerBase
{
    private readonly ClaimAdapterFactory _adapterFactory;
    private readonly IClaimSubmissionService _submissionService;
    private readonly IExplanationOfBenefitProjector _eobProjector;
    private readonly IClaimImportTransactionRepository _importTransactions;
    private readonly ILogger<ClaimsV1Controller> _logger;
    private readonly int _raw837MaxConcurrency;

    public ClaimsV1Controller(
        ClaimAdapterFactory adapterFactory,
        IClaimSubmissionService submissionService,
        IExplanationOfBenefitProjector eobProjector,
        IClaimImportTransactionRepository importTransactions,
        IConfiguration configuration,
        ILogger<ClaimsV1Controller> logger)
    {
        _adapterFactory = adapterFactory;
        _submissionService = submissionService;
        _eobProjector = eobProjector;
        _importTransactions = importTransactions;
        _logger = logger;
        _raw837MaxConcurrency = Math.Clamp(
            configuration.GetValue("ClaimsImport:Raw837MaxConcurrency", 32),
            1,
            64);
    }

    /// <summary>
    /// Submit a new claim through the canonical V1 surface. Accepts
    /// <see cref="AdapterClaim"/> directly so the wire shape stays
    /// stable as the internal <see cref="Claim"/> domain model
    /// evolves. On success, emits a <c>ClaimVersionSubmitted</c>
    /// audit event to the Mongo append-only stream.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(AdapterClaim), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> SubmitClaim(
        [FromBody] AdapterClaim claim,
        CancellationToken ct = default)
    {
        if (claim is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        var tenantId = GetTenantId();

        _logger.LogInformation(
            "v1 claim submission: member={Member}, provider={Provider}, lines={LineCount}",
            SanitizeForLog(claim.MemberId), SanitizeForLog(claim.BillingProviderNPI),
            claim.ClaimLines?.Count ?? 0);

        var actorId = ResolveActorId();
        var correlationId = ResolveCorrelationId();

        var result = await _submissionService.SubmitAsync(
            claim, tenantId, actorId, correlationId, ct);

        if (!result.Success)
        {
            return MapFailure(result);
        }

        var created = result.Claim!;
        return CreatedAtAction(
            nameof(SearchMemberClaims),
            new { memberId = created.MemberId },
            created);
    }

    /// <summary>
    /// Accepts a raw X12 837 EDI file (professional or institutional,
    /// single claim or a multi-claim batch), parses it, maps each parsed
    /// claim onto <see cref="AdapterClaim"/>, and submits each through the
    /// same <see cref="IClaimSubmissionService"/> every other claim on
    /// this surface goes through — the on-ramp for evaluators dropping in
    /// their own 837 file rather than calling <c>POST /api/v1/claims</c>
    /// with an already-structured payload one claim at a time.
    /// </summary>
    [HttpPost("import/raw837")]
    [RequestSizeLimit(20_000_000)]
    [ProducesResponseType(typeof(Raw837ImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Raw837ImportResult>> ImportRaw837(
        [FromForm] IFormFile file,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "A non-empty 837 file is required." });
        }

        string ediContent;
        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            ediContent = await reader.ReadToEndAsync(ct);
        }

        List<CloudHealthOffice.ClaimsScrubEngine.Models.X12837Claim> parsedClaims;
        try
        {
            parsedClaims = ClaimsService.EDI.Inbound.X12837Parser.Parse(ediContent);
        }
        catch (ClaimsService.EDI.Inbound.X12FormatException ex)
        {
            _logger.LogWarning(ex, "Failed to parse uploaded 837 file {FileName}", SanitizeForLog(file.FileName));
            return BadRequest(new { error = $"Could not parse 837 file: {ex.Message}" });
        }

        if (parsedClaims.Count == 0)
        {
            return BadRequest(new { error = "No CLM (claim) segments found in the uploaded file." });
        }

        var tenantId = GetTenantId();
        var actorId = ResolveActorId();
        var correlationId = ResolveCorrelationId();

        _logger.LogInformation(
            "Parsed uploaded 837 file {FileName} for tenant {TenantId}: {Count} claim(s), submitting with max concurrency {MaxConcurrency}",
            SanitizeForLog(file.FileName), SanitizeForLog(tenantId), parsedClaims.Count, _raw837MaxConcurrency);

        var results = new Raw837ClaimResult[parsedClaims.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, parsedClaims.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _raw837MaxConcurrency,
                CancellationToken = ct
            },
            async (index, cancellationToken) =>
        {
            var parsed = parsedClaims[index];
            var adapterClaim = ClaimsService.EDI.Inbound.X12837ClaimMapper.Map(parsed, tenantId);
            var result = await _submissionService.SubmitAsync(
                adapterClaim,
                tenantId,
                actorId,
                correlationId,
                cancellationToken);

            var errors = result.Success
                ? []
                : result.Errors.Select(e => $"{e.Field}: {e.Message}").ToList();

            results[index] = new Raw837ClaimResult
            {
                ClaimNumber = parsed.ClaimId,
                Success = result.Success,
                ClaimId = result.Claim?.Id,
                Errors = errors
            };

            // Persisted regardless of outcome — a rejected claim is exactly
            // what an evaluator troubleshooting a dropped file needs to see.
            try
            {
                await _importTransactions.CreateAsync(new ClaimImportTransaction
                {
                    TenantId = tenantId,
                    ClaimNumber = parsed.ClaimId,
                    ClaimId = result.Claim?.Id,
                    MemberId = adapterClaim.MemberId,
                    FileName = file.FileName,
                    Status = result.Success ? "Accepted" : "Rejected",
                    Errors = errors
                });
            }
            catch (Exception ex)
            {
                // Transaction-log failures must not break the import — log
                // and move on, same posture as enrollment-import-service's
                // RecordTransactionAsync.
                _logger.LogWarning(ex,
                    "Failed to persist ClaimImportTransaction for claim {ClaimNumber}",
                    SanitizeForLog(parsed.ClaimId));
            }
        });

        return Ok(new Raw837ImportResult
        {
            FileName = file.FileName,
            TotalClaims = parsedClaims.Count,
            SucceededCount = results.Count(r => r.Success),
            Results = results.ToList()
        });
    }

    /// <summary>
    /// Most recent 837 import transactions for the tenant, newest first —
    /// the admin-console read path (mirrors enrollment-import-service's
    /// <c>GET /api/v1/enrollment/transactions/recent</c>). Includes
    /// rejected imports, not just successful ones.
    /// </summary>
    [HttpGet("import-transactions")]
    [ProducesResponseType(typeof(List<ClaimImportTransaction>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListImportTransactions([FromQuery] int limit = 100)
    {
        var tenantId = TryGetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "X-Tenant-ID header is required" });
        }
        if (limit < 1 || limit > 500) limit = 100;

        var transactions = await _importTransactions.ListRecentAsync(tenantId, limit);
        return Ok(transactions);
    }

    /// <summary>
    /// Search claims for a member. Returns a small wrapper
    /// <c>{ total, page, pageSize, resources[] }</c> where <c>resources</c>
    /// is a FHIR <c>ExplanationOfBenefit</c> array (one per matching
    /// claim).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(EobSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EobSearchResponse>> SearchMemberClaims(
        [FromQuery, Required] string memberId,
        [FromQuery] DateTime? serviceDateFrom = null,
        [FromQuery] DateTime? serviceDateTo = null,
        [FromQuery] ClaimStatus? status = null,
        [FromQuery] string? providerNPI = null,
        [FromQuery] ClaimType? claimType = null,
        [FromQuery] decimal? amountMin = null,
        [FromQuery] decimal? amountMax = null,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberId))
            return BadRequest(new { error = "memberId is required" });

        var tenantId = TryGetTenantId();

        _logger.LogInformation(
            "v1 claims member search: member={Member}, status={Status}, type={Type}, amount=[{Min},{Max}]",
            SanitizeForLog(memberId), status, claimType, amountMin, amountMax);

        var adapter = await _adapterFactory.GetAdapterAsync(tenantId, ct);
        var adapterResponse = await adapter.SearchClaimsForMemberAsync(
            new ClaimMemberSearchAdapterRequest
            {
                TenantId = tenantId,
                MemberId = memberId,
                ServiceDateFrom = serviceDateFrom,
                ServiceDateTo = serviceDateTo,
                Status = status,
                ProviderNPI = providerNPI,
                ClaimType = claimType,
                AmountMin = amountMin,
                AmountMax = amountMax,
                Page = page,
                PageSize = pageSize,
            },
            ct);

        var resources = new JsonArray();
        foreach (var adapterClaim in adapterResponse.Claims)
        {
            // Round-trip AdapterClaim → Claim for the existing projector
            // contract. The 5.2 mapper is loss-less per
            // SubmitClaimAsync_round_trips_AdapterClaim_losslessly.
            // Capability 5.11 may evolve the projector to consume
            // AdapterClaim directly; that's 5.11's scope.
            resources.Add(_eobProjector.Project(adapterClaim.ToClaim()));
        }

        // Adapters that don't surface a TotalCount fall back to the page
        // size — defensive, only reachable for vendor adapters that
        // currently throw NotImplementedException on this method anyway.
        var total = adapterResponse.TotalCount ?? adapterResponse.Claims.Count;

        return Ok(new EobSearchResponse
        {
            Total = total,
            Page = page,
            PageSize = pageSize,
            Resources = resources
        });
    }

    private IActionResult MapFailure(ClaimSubmissionResult result)
    {
        var errors = result.Errors.Select(e => new
        {
            field = e.Field,
            code = e.Code,
            message = e.Message
        });

        return result.FailureKind switch
        {
            ClaimSubmissionFailureKind.NotImplemented => StatusCode(
                StatusCodes.Status501NotImplemented,
                new
                {
                    error = "Claim submission is not implemented for this tenant's configured platform",
                    errors
                }),
            _ => BadRequest(new
            {
                error = "Claim submission validation failed",
                errors
            }),
        };
    }

    private string GetTenantId()
    {
        var tenantId = HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new InvalidOperationException(
                "TenantId not found in HttpContext. Ensure tenant middleware is configured.");
        }
        return tenantId;
    }

    private string TryGetTenantId() =>
        HttpContext?.Items["TenantId"]?.ToString() ?? string.Empty;

    private string ResolveActorId()
    {
        var sub = HttpContext.User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(sub)) return sub;
        if (HttpContext.Request.Headers.TryGetValue("X-User-Id", out var header) &&
            !string.IsNullOrEmpty(header.ToString()))
        {
            return header.ToString();
        }
        return "system";
    }

    private string? ResolveCorrelationId()
    {
        if (HttpContext.Request.Headers.TryGetValue("X-Correlation-Id", out var header) &&
            !string.IsNullOrEmpty(header.ToString()))
        {
            return header.ToString();
        }
        return Activity.Current?.Id;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// Wrapper for FHIR ExplanationOfBenefit search results. Kept intentionally
/// small — pagination metadata plus the FHIR resource array as a JsonNode so
/// we can project without taking on the Hl7.Fhir.R4 transitive dep graph.
/// </summary>
public class EobSearchResponse
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public JsonArray Resources { get; set; } = new();
}

/// <summary>Per-file result of a raw 837 upload — one entry per CLM segment found, in order.</summary>
public class Raw837ImportResult
{
    public string FileName { get; set; } = string.Empty;
    public int TotalClaims { get; set; }
    public int SucceededCount { get; set; }
    public List<Raw837ClaimResult> Results { get; set; } = [];
}

public class Raw837ClaimResult
{
    public string ClaimNumber { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ClaimId { get; set; }
    public List<string> Errors { get; set; } = [];
}
