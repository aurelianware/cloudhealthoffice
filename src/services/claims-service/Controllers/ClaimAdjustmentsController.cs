using System.Diagnostics;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// Operator-initiated adjustment workflow surface (capability 5.12a).
/// Three endpoints:
/// <list type="bullet">
///   <item><c>POST /api/v1/claims/{predecessorClaimId}/adjustments</c> — create adjustment (Decision 6 idempotent on <c>Idempotency-Key</c> header).</item>
///   <item><c>GET /api/v1/claims/{predecessorClaimId}/adjustments</c> — list adjustments scoped to a predecessor (Phase 1 returns 0 or 1 per Decision 11).</item>
///   <item><c>GET /api/v1/adjustments/{id}</c> — fetch a single adjustment by id.</item>
/// </list>
///
/// <para>Gap 3 list filter (<c>GET /api/v1/adjustments?status=...</c>) is
/// served by the same controller via the
/// <see cref="ListAdjustments"/> action.</para>
/// </summary>
[ApiController]
[Produces("application/json")]
public class ClaimAdjustmentsController : ControllerBase
{
    private readonly IClaimAdjustmentService _adjustmentService;
    private readonly IClaimAdjustmentRepository _adjustmentRepository;

    public ClaimAdjustmentsController(
        IClaimAdjustmentService adjustmentService,
        IClaimAdjustmentRepository adjustmentRepository)
    {
        _adjustmentService = adjustmentService;
        _adjustmentRepository = adjustmentRepository;
    }

    /// <summary>
    /// Create a new adjustment for the predecessor claim. Per Decision 6
    /// the <c>Idempotency-Key</c> header is required — same key + same
    /// body returns the existing adjustment with 200; same key +
    /// different body returns 409 Conflict.
    /// </summary>
    [HttpPost("api/v1/claims/{predecessorClaimId}/adjustments")]
    [ProducesResponseType(typeof(ClaimAdjustmentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ClaimAdjustmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> CreateAdjustment(
        [FromRoute] string predecessorClaimId,
        [FromBody] ClaimAdjustmentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct = default)
    {
        if (request == null)
        {
            return BadRequest(new { error = "Request body is required" });
        }
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return BadRequest(new { error = "Idempotency-Key header is required" });
        }
        if (!ModelState.IsValid)
        {
            // ValidationProblem produces a sanitized ProblemDetails body
            // that surfaces only validation errors — never the inbound
            // payload. Echoing ModelState directly can leak PHI from the
            // submitted claim into the 400 response.
            return ValidationProblem(ModelState);
        }

        var tenantId = GetTenantId();
        var actorId = ResolveActorId();
        var correlationId = ResolveCorrelationId();

        var result = await _adjustmentService.CreateAdjustmentAsync(
            predecessorClaimId, request, idempotencyKey, tenantId, actorId, correlationId, ct);

        return result.Outcome switch
        {
            ClaimAdjustmentOutcome.Created => CreatedAtAction(
                nameof(GetAdjustment),
                new { id = result.Adjustment!.Id },
                ClaimAdjustmentResponse.FromDomain(result.Adjustment!)),
            ClaimAdjustmentOutcome.AlreadyExists => Ok(ClaimAdjustmentResponse.FromDomain(result.Adjustment!)),
            ClaimAdjustmentOutcome.IdempotencyConflict => Conflict(new
            {
                error = result.Message,
                existingAdjustmentId = result.Adjustment!.Id,
            }),
            ClaimAdjustmentOutcome.ConflictingAdjustment => Conflict(new
            {
                error = result.Message,
                existingAdjustmentId = result.Adjustment!.Id,
                existingStatus = result.Adjustment.Status.ToString(),
            }),
            ClaimAdjustmentOutcome.PredecessorNotFound => NotFound(new { error = result.Message }),
            ClaimAdjustmentOutcome.InvalidSourceState => UnprocessableEntity(new
            {
                error = result.Message,
                predecessorStatus = result.Predecessor!.Status.ToString(),
            }),
            ClaimAdjustmentOutcome.DepthLimitExceeded => UnprocessableEntity(new
            {
                error = result.Message,
                predecessorClaimId = result.Predecessor!.Id,
            }),
            ClaimAdjustmentOutcome.SubmissionFailed => result.SubmissionFailureKind switch
            {
                ClaimSubmissionFailureKind.NotImplemented => StatusCode(
                    StatusCodes.Status501NotImplemented,
                    new
                    {
                        error = result.Message,
                        errors = result.SubmissionErrors.Select(e => new { field = e.Field, code = e.Code, message = e.Message }),
                    }),
                _ => BadRequest(new
                {
                    error = result.Message,
                    errors = result.SubmissionErrors.Select(e => new { field = e.Field, code = e.Code, message = e.Message }),
                }),
            },
            _ => StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "Unhandled adjustment outcome" }),
        };
    }

    /// <summary>
    /// List adjustments scoped to a single predecessor claim. Phase 1
    /// returns at most one row per Decision 11 (depth=1 invariant).
    /// </summary>
    [HttpGet("api/v1/claims/{predecessorClaimId}/adjustments")]
    [ProducesResponseType(typeof(ClaimAdjustmentListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAdjustmentsForClaim(
        [FromRoute] string predecessorClaimId,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var (page, total) = await _adjustmentRepository.ListAsync(
            tenantId,
            new ClaimAdjustmentListFilter
            {
                PredecessorClaimId = predecessorClaimId,
                Page = 1,
                PageSize = 50,
            },
            ct);

        return Ok(new ClaimAdjustmentListResponse
        {
            Total = total,
            Page = 1,
            PageSize = 50,
            Items = page.Select(ClaimAdjustmentResponse.FromDomain).ToList(),
        });
    }

    /// <summary>
    /// Filtered list across all adjustments for the tenant (Gap 3
    /// ratification). Consumed by the future 5.12b ReversalRunService
    /// for batch creation.
    /// </summary>
    [HttpGet("api/v1/adjustments")]
    [ProducesResponseType(typeof(ClaimAdjustmentListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAdjustments(
        [FromQuery] ClaimAdjustmentListFilter filter,
        CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            // ValidationProblem produces a sanitized ProblemDetails body
            // that surfaces only validation errors — never the inbound
            // payload. Echoing ModelState directly can leak PHI from the
            // submitted claim into the 400 response.
            return ValidationProblem(ModelState);
        }

        var tenantId = GetTenantId();
        var (page, total) = await _adjustmentRepository.ListAsync(tenantId, filter, ct);

        return Ok(new ClaimAdjustmentListResponse
        {
            Total = total,
            Page = filter.Page,
            PageSize = filter.PageSize,
            Items = page.Select(ClaimAdjustmentResponse.FromDomain).ToList(),
        });
    }

    [HttpGet("api/v1/adjustments/{id}")]
    [ProducesResponseType(typeof(ClaimAdjustmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAdjustment(
        [FromRoute] string id,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var adjustment = await _adjustmentRepository.GetByIdAsync(tenantId, id, ct);
        if (adjustment == null)
        {
            return NotFound(new { error = $"Adjustment {id} not found" });
        }
        return Ok(ClaimAdjustmentResponse.FromDomain(adjustment));
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
}

/// <summary>
/// Wire-shape DTO for the adjustment surface. Mirrors
/// <see cref="ClaimAdjustment"/> minus the IdempotencyKey/RequestHash
/// internal-state fields.
/// </summary>
public class ClaimAdjustmentResponse
{
    public string Id { get; set; } = string.Empty;
    public string ClaimVersionId { get; set; } = string.Empty;
    public string PredecessorClaimId { get; set; } = string.Empty;
    public string PredecessorVersionId { get; set; } = string.Empty;
    public string NewClaimId { get; set; } = string.Empty;
    public string AdjustmentReason { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public ClaimAdjustmentStatus Status { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadjudicationCompletedAt { get; set; }
    public DateTime? ReversalCompletedAt { get; set; }
    public string? ReversalRunId { get; set; }
    public string? FailureReason { get; set; }

    public static ClaimAdjustmentResponse FromDomain(ClaimAdjustment src) => new()
    {
        Id = src.Id,
        ClaimVersionId = src.ClaimVersionId,
        PredecessorClaimId = src.PredecessorClaimId,
        PredecessorVersionId = src.PredecessorVersionId,
        NewClaimId = src.NewClaimId,
        AdjustmentReason = src.AdjustmentReason,
        Notes = src.Notes,
        Status = src.Status,
        CreatedBy = src.CreatedBy,
        CreatedAt = src.CreatedAt,
        ReadjudicationCompletedAt = src.ReadjudicationCompletedAt,
        ReversalCompletedAt = src.ReversalCompletedAt,
        ReversalRunId = src.ReversalRunId,
        FailureReason = src.FailureReason,
    };
}

public class ClaimAdjustmentListResponse
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<ClaimAdjustmentResponse> Items { get; set; } = new();
}
