using Microsoft.AspNetCore.Mvc;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// Credentialing workflow REST surface (capability 5.6). Operates on the
/// append-only credentialing event chain rooted at
/// <c>/api/v1/providers/{id}/credentialing/...</c>. Status is a projection
/// of the chain — see <c>docs/architecture/credentialing-workflow.md</c>.
/// </summary>
[ApiController]
[Route("api/v1/providers/{id}/credentialing")]
[Produces("application/json")]
public sealed class CredentialingController : ControllerBase
{
    private readonly ICredentialingService _credentialing;
    private readonly ILogger<CredentialingController> _logger;

    public CredentialingController(
        ICredentialingService credentialing,
        ILogger<CredentialingController> logger)
    {
        _credentialing = credentialing;
        _logger = logger;
    }

    private string TenantId =>
        HttpContext.Items["TenantId"]?.ToString()
            ?? throw new InvalidOperationException("TenantId not found in request context");

    /// <summary>Submit a new credentialing application (opens a chain).</summary>
    [HttpPost("applications")]
    [ProducesResponseType(typeof(CredentialingEvent), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CredentialingEvent>> SubmitApplication(
        string id,
        [FromBody] SubmitApplicationRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Submitting credentialing application for provider {Id}, source={Source}",
            SanitizeForLog(id), SanitizeForLog(request?.ApplicationSource));

        try
        {
            var evt = await _credentialing.SubmitApplicationAsync(
                TenantId, id, request, ResolveActorId(), HttpContext.TraceIdentifier, ct);
            return Created(string.Empty, evt);
        }
        catch (CredentialingNotFoundException ex)
        {
            return NotFound(new { error = "provider_not_found", message = ex.Message });
        }
        catch (CredentialingValidationException ex)
        {
            return BadRequest(new { error = "credentialing_validation_failed", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "credentialing_publish_failed",
                message = ex.Message,
            });
        }
    }

    /// <summary>Withdraw the open credentialing application.</summary>
    [HttpPost("applications/{eventId}/withdraw")]
    [ProducesResponseType(typeof(CredentialingEvent), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CredentialingEvent>> WithdrawApplication(
        string id,
        string eventId,
        [FromBody] WithdrawApplicationRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Withdrawing credentialing application {EventId} for provider {Id}",
            SanitizeForLog(eventId), SanitizeForLog(id));

        try
        {
            var evt = await _credentialing.WithdrawApplicationAsync(
                TenantId, id, eventId, request, ResolveActorId(), HttpContext.TraceIdentifier, ct);
            return Ok(evt);
        }
        catch (CredentialingNotFoundException ex)
        {
            return NotFound(new { error = "provider_not_found", message = ex.Message });
        }
        catch (CredentialingValidationException ex)
        {
            return BadRequest(new { error = "credentialing_validation_failed", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "credentialing_publish_failed",
                message = ex.Message,
            });
        }
    }

    /// <summary>Record primary-source verification completion.</summary>
    [HttpPost("verifications")]
    [ProducesResponseType(typeof(CredentialingEvent), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CredentialingEvent>> RecordPrimarySourceVerification(
        string id,
        [FromBody] RecordPrimarySourceVerificationRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Recording primary-source verification for provider {Id}, vendor={Vendor}",
            SanitizeForLog(id), SanitizeForLog(request?.VerificationVendor));

        try
        {
            var evt = await _credentialing.RecordPrimarySourceVerificationAsync(
                TenantId, id, request, ResolveActorId(), HttpContext.TraceIdentifier, ct);
            return Created(string.Empty, evt);
        }
        catch (CredentialingNotFoundException ex)
        {
            return NotFound(new { error = "provider_not_found", message = ex.Message });
        }
        catch (CredentialingValidationException ex)
        {
            return BadRequest(new { error = "credentialing_validation_failed", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "credentialing_publish_failed",
                message = ex.Message,
            });
        }
    }

    /// <summary>Schedule a committee review for the open application.</summary>
    [HttpPost("committee-reviews")]
    [ProducesResponseType(typeof(CredentialingEvent), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CredentialingEvent>> ScheduleCommitteeReview(
        string id,
        [FromBody] ScheduleCommitteeReviewRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Scheduling committee review for provider {Id}, committee={Committee}",
            SanitizeForLog(id), SanitizeForLog(request?.CommitteeId));

        try
        {
            var evt = await _credentialing.ScheduleCommitteeReviewAsync(
                TenantId, id, request, ResolveActorId(), HttpContext.TraceIdentifier, ct);
            return Created(string.Empty, evt);
        }
        catch (CredentialingNotFoundException ex)
        {
            return NotFound(new { error = "provider_not_found", message = ex.Message });
        }
        catch (CredentialingValidationException ex)
        {
            return BadRequest(new { error = "credentialing_validation_failed", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "credentialing_publish_failed",
                message = ex.Message,
            });
        }
    }

    /// <summary>Record a credentialing committee decision.</summary>
    [HttpPost("decisions")]
    [ProducesResponseType(typeof(CredentialingEvent), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CredentialingEvent>> RecordDecision(
        string id,
        [FromBody] RecordDecisionRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Recording credentialing decision for provider {Id}, decision={Decision}, authority={Authority}",
            SanitizeForLog(id), request?.Decision, request?.DecisionAuthorityType);

        try
        {
            var evt = await _credentialing.RecordDecisionAsync(
                TenantId, id, request, ResolveActorId(), HttpContext.TraceIdentifier, ct);
            return Created(string.Empty, evt);
        }
        catch (CredentialingNotFoundException ex)
        {
            return NotFound(new { error = "provider_not_found", message = ex.Message });
        }
        catch (CredentialingValidationException ex)
        {
            return BadRequest(new { error = "credentialing_validation_failed", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "credentialing_publish_failed",
                message = ex.Message,
            });
        }
    }

    /// <summary>Trigger the re-credentialing cycle (opens a new chain linked to the predecessor approval).</summary>
    [HttpPost("recredential")]
    [ProducesResponseType(typeof(CredentialingEvent), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CredentialingEvent>> TriggerRecredentialing(
        string id,
        [FromBody] TriggerRecredentialingRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Triggering re-credentialing for provider {Id}, reason={Reason}",
            SanitizeForLog(id), SanitizeForLog(request?.Reason));

        try
        {
            var evt = await _credentialing.TriggerRecredentialingAsync(
                TenantId, id, request, ResolveActorId(), HttpContext.TraceIdentifier, ct);
            return Created(string.Empty, evt);
        }
        catch (CredentialingNotFoundException ex)
        {
            return NotFound(new { error = "provider_not_found", message = ex.Message });
        }
        catch (CredentialingValidationException ex)
        {
            return BadRequest(new { error = "credentialing_validation_failed", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "credentialing_publish_failed",
                message = ex.Message,
            });
        }
    }

    /// <summary>Current projected credentialing status for the provider.</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(CredentialingProjectionResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<CredentialingProjectionResult>> GetStatus(
        string id,
        CancellationToken ct)
    {
        var result = await _credentialing.GetCurrentStatusAsync(TenantId, id, ct);
        return Ok(result);
    }

    /// <summary>
    /// Projected credentialing status for the provider as of the supplied
    /// date (capability 5.6). Consumer-facing surface for claims-service
    /// adjudication: the <paramref name="asOfDate"/> is the claim's
    /// service date, anchoring credentialing-status enforcement to when
    /// the service was rendered rather than when the claim is processed.
    /// Returns a trimmed projection (see
    /// <see cref="CredentialingStatusResponse"/>) — internal projection
    /// fields like <c>CurrentApplicationEventId</c> belong to the admin
    /// <c>/status</c> + <c>/history</c> surface, not to cross-service
    /// enforcement.
    /// </summary>
    [HttpGet("status-as-of")]
    [ProducesResponseType(typeof(CredentialingStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CredentialingStatusResponse>> GetStatusAsOf(
        string id,
        [FromQuery] DateTime? asOfDate,
        CancellationToken ct)
    {
        if (asOfDate is null)
        {
            return BadRequest(new
            {
                error = "missing_as_of_date",
                message = "asOfDate query parameter is required (ISO-8601 UTC).",
            });
        }

        // Normalize Unspecified-kind input to UTC. Inbound query strings
        // typically deserialize with Kind=Unspecified; treating that as
        // UTC matches how the projector interprets stored decision dates
        // (see CredentialingProjector.ComputeStatus).
        var asOfUtc = asOfDate.Value.Kind switch
        {
            DateTimeKind.Utc => asOfDate.Value,
            DateTimeKind.Local => asOfDate.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(asOfDate.Value, DateTimeKind.Utc),
        };

        var projection = await _credentialing.GetStatusAsOfAsync(
            TenantId, id, new DateTimeOffset(asOfUtc, TimeSpan.Zero), ct);

        return Ok(new CredentialingStatusResponse
        {
            ProviderId = id,
            AsOfDate = asOfUtc,
            Status = projection.Status.ToString(),
            CredentialingDate = projection.CredentialingDate,
            RecredentialingDueDate = projection.RecredentialingDueDate,
            LastDecisionAuthorityId = projection.LastDecisionAuthorityId,
            LastDecisionAuthorityType = projection.LastDecisionAuthorityType?.ToString(),
            LastDecidedAt = projection.LastDecidedAt,
        });
    }

    /// <summary>Newest-first paged history of credentialing events for the provider.</summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(CredentialingHistoryPage), StatusCodes.Status200OK)]
    public async Task<ActionResult<CredentialingHistoryPage>> GetHistory(
        string id,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var page = await _credentialing.GetHistoryAsync(TenantId, id, cursor, limit, ct);
        return Ok(page);
    }

    private string ResolveActorId()
    {
        var sub = HttpContext.User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(sub)) return sub;
        if (HttpContext.Request.Headers.TryGetValue("X-User-Id", out var header) && !string.IsNullOrEmpty(header.ToString()))
            return header.ToString();
        return "system";
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
