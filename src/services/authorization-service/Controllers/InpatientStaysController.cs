using AuthorizationService.Exceptions;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.CareManagement;
using AuthorizationService.Services.ConcurrentReview;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace AuthorizationService.Controllers;

/// <summary>
/// Inpatient stays and concurrent review.
///
/// The tenant and the acting user come from the validated CHO token only.
/// Reads need <c>authorizations:read</c> and writes <c>authorizations:write</c>
/// (the service defaults); recording a review is a determination, so it
/// additionally needs <c>authorizations:decide</c>.
/// </summary>
[ApiController]
[Route("api/inpatient-stays")]
[Produces("application/json")]
public sealed class InpatientStaysController(
    IInpatientStayRepository stays,
    IConcurrentReviewService reviews,
    ICurrentActor currentActor,
    ILogger<InpatientStaysController> logger,
    DischargeCareReferral? careReferral = null) : ControllerBase
{
    private string Actor => currentActor.UserId;

    /// <summary>
    /// The inpatient census: open stays with days elapsed against days
    /// certified, the next review due, and what is already overdue. Lapsed
    /// certifications sort first, then overdue reviews, then whatever falls
    /// due soonest.
    /// </summary>
    [HttpGet("census")]
    [ProducesResponseType(typeof(InpatientCensus), StatusCodes.Status200OK)]
    public async Task<ActionResult<InpatientCensus>> GetCensus(
        [FromQuery] DateTime? asOf, [FromQuery] string? facilityNpi, CancellationToken ct)
    {
        // A date off the query string binds as Unspecified, and ToUniversalTime()
        // would read that as the server's local time and shift the census by
        // the host's offset. The census is a calendar day, so an unqualified
        // date is taken as the UTC day it names.
        var asOfUtc = asOf switch
        {
            null => DateTime.UtcNow,
            { Kind: DateTimeKind.Utc } utc => utc,
            { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
            var unspecified => DateTime.SpecifyKind(unspecified.Value, DateTimeKind.Utc),
        };

        var census = await reviews.GetCensusAsync(asOfUtc, facilityNpi, ct);
        return Ok(census);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(InpatientStay), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InpatientStay>> GetById(string id, CancellationToken ct) =>
        await stays.GetByIdAsync(id, ct) is { } stay ? Ok(stay) : NotFound();

    /// <summary>Every stay recorded against one authorization.</summary>
    [HttpGet("by-authorization/{authorizationId}")]
    [ProducesResponseType(typeof(IReadOnlyList<InpatientStay>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<InpatientStay>>> ListByAuthorization(
        string authorizationId, CancellationToken ct) =>
        Ok(await stays.ListByAuthorizationAsync(authorizationId, ct));

    /// <summary>
    /// Opens a stay against an existing authorization. An authorization that
    /// already certified a period through or past the admission date carries
    /// that period onto the stay, so the first concurrent review extends a
    /// certification rather than making one.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(InpatientStay), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InpatientStay>> Admit(
        [FromBody] AdmitStayRequest request, CancellationToken ct)
    {
        try
        {
            var stay = await reviews.AdmitAsync(request, Actor, ct);
            return CreatedAtAction(nameof(GetById), new { id = stay.Id }, stay);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails { Title = "Authorization not found", Detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid admission", Detail = ex.Message });
        }
    }

    /// <summary>
    /// Evaluates clinical criteria for a stay as it currently stands, without
    /// deciding anything. A criteria source that is unconfigured or
    /// unreachable answers "indeterminate" with a reason, never a denial.
    /// </summary>
    [HttpPost("{id}/criteria-evaluation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EvaluateCriteria(string id, CancellationToken ct)
    {
        if (await stays.GetByIdAsync(id, ct) is not { } stay) return NotFound();
        return Ok(await reviews.EvaluateCriteriaAsync(stay, ct));
    }

    /// <summary>
    /// Records a concurrent review. Certified days are contiguous: each
    /// approving review begins where the last certification ended, so the
    /// certified-through date moves forward and never overlaps.
    /// </summary>
    /// <remarks>
    /// An adverse outcome (modified or denied) requires a physician reviewer
    /// and a reason, and is refused without them.
    /// </remarks>
    [RequirePermission(AuthorizationsController.DecidePermission)]
    [HttpPost("{id}/reviews")]
    [ProducesResponseType(typeof(InpatientStay), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InpatientStay>> RecordReview(
        string id, [FromBody] RecordReviewRequest request, CancellationToken ct)
    {
        if (await stays.GetByIdAsync(id, ct) is not { } stay) return NotFound();

        try
        {
            var updated = await reviews.RecordReviewAsync(
                stay, request.ToDecision(), request.EvaluateCriteria, Actor, ct);
            return Ok(updated);
        }
        catch (InpatientStayTransitionException ex)
        {
            return Conflict(new ProblemDetails { Title = "Stay cannot be reviewed", Detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid review", Detail = ex.Message });
        }
        catch (InpatientStayConcurrencyException ex)
        {
            // The id comes off the route, so it is sanitized before it reaches a log line.
            logger.LogInformation(
                "Concurrent review on stay {StayId} lost a write race: {Message}",
                LogSanitizer.Sanitize(id), ex.Message);
            return Conflict(new ProblemDetails { Title = "Stay changed", Detail = ex.Message });
        }
    }

    /// <summary>Records a discharge. No further review falls due on the stay.</summary>
    [RequirePermission(AuthorizationsController.DecidePermission)]
    [HttpPost("{id}/discharge")]
    [ProducesResponseType(typeof(InpatientStay), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InpatientStay>> Discharge(
        string id, [FromBody] DischargeRequest request, CancellationToken ct)
    {
        if (await stays.GetByIdAsync(id, ct) is not { } stay) return NotFound();

        try
        {
            var discharged = await reviews.DischargeAsync(
                stay, request.DischargeDate, request.DischargeDisposition, Actor, ct);

            // Transitions of care: the window a readmission comes from. The
            // referral is best-effort and runs after the discharge is
            // persisted, so a care management service that is down cannot
            // look like a failed discharge.
            if (careReferral is not null)
                await careReferral.ReferIfWarrantedAsync(discharged, ct);

            return Ok(discharged);
        }
        catch (InpatientStayTransitionException ex)
        {
            return Conflict(new ProblemDetails { Title = "Stay cannot be discharged", Detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid discharge", Detail = ex.Message });
        }
        catch (InpatientStayConcurrencyException ex)
        {
            return Conflict(new ProblemDetails { Title = "Stay changed", Detail = ex.Message });
        }
    }
}

/// <summary>A concurrent-review decision as it arrives over HTTP.</summary>
public sealed class RecordReviewRequest
{
    public ConcurrentReviewOutcome Outcome { get; set; }
    public int DaysCertified { get; set; }
    public string? LevelOfCare { get; set; }
    public DateTime? NextReviewDueDate { get; set; }
    public string? ReviewerName { get; set; }
    public bool PhysicianReviewed { get; set; }
    public string? DenialReasonCode { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// Consult the configured criteria source as part of recording this
    /// review, and cite what it said on the record. A citation supplied in
    /// this request wins: the reviewer may be recording a guideline consulted
    /// outside Cloud Health Office.
    /// </summary>
    public bool EvaluateCriteria { get; set; }

    public string? CriteriaProviderId { get; set; }
    public string? CriteriaGuidelineId { get; set; }
    public string? CriteriaGuidelineVersion { get; set; }
    public string? CriteriaRecommendation { get; set; }
    public List<string>? CriteriaRationale { get; set; }

    internal ConcurrentReviewDecision ToDecision() => new()
    {
        Outcome                  = Outcome,
        DaysCertified            = DaysCertified,
        LevelOfCare              = LevelOfCare,
        NextReviewDueDate        = NextReviewDueDate,
        ReviewerName             = ReviewerName,
        PhysicianReviewed        = PhysicianReviewed,
        DenialReasonCode         = DenialReasonCode,
        Reason                   = Reason,
        CriteriaProviderId       = CriteriaProviderId,
        CriteriaGuidelineId      = CriteriaGuidelineId,
        CriteriaGuidelineVersion = CriteriaGuidelineVersion,
        CriteriaRecommendation   = CriteriaRecommendation,
        CriteriaRationale        = CriteriaRationale ?? [],
    };
}

public sealed class DischargeRequest
{
    public DateTime DischargeDate { get; set; }

    /// <summary>UB-04 FL17 patient discharge status, e.g. 01 home, 03 SNF.</summary>
    public string? DischargeDisposition { get; set; }
}
