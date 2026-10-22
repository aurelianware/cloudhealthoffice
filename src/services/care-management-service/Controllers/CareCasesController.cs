using CareManagementService.Models;
using CareManagementService.Repositories;
using CareManagementService.Services;
using CareManagementService.Services.Work;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace CareManagementService.Controllers;

/// <summary>
/// Care management cases: referral, screening, outreach, the care plan, and
/// the activities that carry it out.
///
/// The tenant and the acting user come from the validated CHO token only;
/// request bodies never name either. Reads need <c>care:read</c> and writes
/// <c>care:write</c> (the defaults set in Program.cs).
/// </summary>
[ApiController]
[Route("api/care-cases")]
[Produces("application/json")]
public sealed class CareCasesController(
    ICareCaseRepository cases,
    ICareWorkItemGateway work,
    ICurrentActor currentActor,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private string Actor => currentActor.UserId;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ── Reading ───────────────────────────────────────────────────

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(CareCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CareCase>> GetById(string id, CancellationToken ct) =>
        await cases.GetByIdAsync(id, ct) is { } careCase ? Ok(careCase) : NotFound();

    /// <summary>Every case for one member, newest referral first.</summary>
    [HttpGet("by-member/{memberId}")]
    [ProducesResponseType(typeof(IReadOnlyList<CareCaseSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CareCaseSummary>>> ListByMember(
        string memberId, CancellationToken ct) =>
        Ok((await cases.ListByMemberAsync(memberId, ct)).Select(Summarize).ToList());

    /// <summary>
    /// A care manager's open caseload, or every open case when no care
    /// manager is named. Highest acuity first, then whatever is due soonest.
    /// </summary>
    [HttpGet("caseload")]
    [ProducesResponseType(typeof(IReadOnlyList<CareCaseSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CareCaseSummary>>> GetCaseload(
        [FromQuery] string? careManagerId,
        [FromQuery] CareProgram? program,
        [FromQuery] int limit = 200,
        CancellationToken ct = default) =>
        Ok((await cases.ListOpenAsync(careManagerId, program, limit, ct)).Select(Summarize).ToList());

    /// <summary>Open cases whose next contact date has passed.</summary>
    [HttpGet("contact-overdue")]
    [ProducesResponseType(typeof(IReadOnlyList<CareCaseSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CareCaseSummary>>> GetContactOverdue(CancellationToken ct) =>
        Ok((await cases.ListContactOverdueAsync(Now, ct)).Select(Summarize).ToList());

    // ── The case ──────────────────────────────────────────────────

    /// <summary>
    /// Opens a case. It starts as Referred: screening, outreach and the
    /// member's consent come before it can be made active.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CareCase), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CareCase>> Open(
        [FromBody] OpenCareCaseRequest request, CancellationToken ct)
    {
        var now = Now;

        var careCase = new CareCase
        {
            CaseNumber         = request.CaseNumber ?? $"CM-{now:yyyyMMdd}-{Guid.NewGuid().ToString("n")[..8].ToUpperInvariant()}",
            MemberId           = request.MemberId,
            MemberFirstName    = request.MemberFirstName,
            MemberLastName     = request.MemberLastName,
            Program            = request.Program,
            Acuity             = request.Acuity,
            ReferralSource     = request.ReferralSource,
            ReferralReference  = request.ReferralReference,
            ReferralReason     = request.ReferralReason,
            CareManagerId      = request.CareManagerId,
            CareManagerName    = request.CareManagerName,
            NextContactDueDate = request.NextContactDueDate,
            Notes              = request.Notes,
            Status             = CareCaseStatus.Referred,
            ReferredDate       = now,
            CreatedBy          = Actor,
            LastUpdatedBy      = Actor,
            StatusHistory      =
            [
                new CareCaseStatusChange
                {
                    Status = CareCaseStatus.Referred,
                    Reason = request.ReferralReason,
                    ChangedAt = now,
                    ChangedBy = Actor,
                },
            ],
        };

        try
        {
            var created = await cases.CreateAsync(careCase, ct);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (DuplicateCaseNumberException ex)
        {
            return Conflict(new ProblemDetails { Title = "Duplicate case number", Detail = ex.Message });
        }
    }

    /// <summary>Moves the case. Closing requires an outcome; declining requires the member's reason.</summary>
    [HttpPut("{id}/status")]
    [ProducesResponseType(typeof(CareCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CareCase>> SetStatus(
        string id, [FromBody] CareCaseStatusRequest request, CancellationToken ct)
    {
        if (await cases.GetByIdAsync(id, ct) is not { } careCase) return NotFound();

        try
        {
            CareCaseLifecycle.Transition(
                careCase, request.Status, Now, Actor, request.Reason, request.ClosureOutcome);
        }
        catch (CareCaseTransitionException ex)
        {
            return Conflict(new ProblemDetails { Title = "Transition not allowed", Detail = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ProblemDetails { Title = "Transition not allowed", Detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid status change", Detail = ex.Message });
        }

        return await SaveAsync(careCase, ct);
    }

    /// <summary>Records that the member agreed to take part. Required before the case can be made active.</summary>
    [HttpPost("{id}/consent")]
    [ProducesResponseType(typeof(CareCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CareCase>> RecordConsent(string id, CancellationToken ct)
    {
        if (await cases.GetByIdAsync(id, ct) is not { } careCase) return NotFound();

        try
        {
            CareCaseLifecycle.RecordConsent(careCase, Now, Actor);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ProblemDetails { Title = "Consent cannot be recorded", Detail = ex.Message });
        }

        return await SaveAsync(careCase, ct);
    }

    // ── The care plan ─────────────────────────────────────────────

    /// <summary>
    /// Replaces the case's care plan. The plan is validated as a whole —
    /// goals must address problems on the plan and interventions must serve
    /// goals on it — because the parts reference each other by id.
    /// </summary>
    [HttpPut("{id}/care-plan")]
    [ProducesResponseType(typeof(CareCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CareCase>> SetCarePlan(
        string id, [FromBody] CarePlan plan, CancellationToken ct)
    {
        if (await cases.GetByIdAsync(id, ct) is not { } careCase) return NotFound();

        try
        {
            CarePlanPolicy.Validate(plan);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid care plan", Detail = ex.Message });
        }

        // The plan is activated through its own endpoint, which checks
        // consent: a PUT cannot activate one by setting a status field.
        plan.Status = careCase.CarePlan?.Status ?? CarePlanStatus.Draft;
        plan.ActivatedDate = careCase.CarePlan?.ActivatedDate;
        plan.CreatedDate = careCase.CarePlan?.CreatedDate ?? Now;
        plan.LastUpdatedDate = Now;
        plan.LastUpdatedBy = Actor;

        careCase.CarePlan = plan;
        careCase.LastUpdatedDate = Now;
        careCase.LastUpdatedBy = Actor;

        return await SaveAsync(careCase, ct);
    }

    /// <summary>Activates the plan with the member. Needs consent and at least one goal.</summary>
    [HttpPost("{id}/care-plan/activate")]
    [ProducesResponseType(typeof(CareCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CareCase>> ActivateCarePlan(string id, CancellationToken ct)
    {
        if (await cases.GetByIdAsync(id, ct) is not { } careCase) return NotFound();

        try
        {
            CarePlanPolicy.Activate(careCase, Now, Actor);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ProblemDetails { Title = "Care plan cannot be activated", Detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid care plan", Detail = ex.Message });
        }

        return await SaveAsync(careCase, ct);
    }

    /// <summary>Records progress against one goal. The note is required.</summary>
    [HttpPost("{id}/care-plan/goals/{goalId}/progress")]
    [ProducesResponseType(typeof(CareCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CareCase>> RecordGoalProgress(
        string id, string goalId, [FromBody] GoalProgressRequest request, CancellationToken ct)
    {
        if (await cases.GetByIdAsync(id, ct) is not { } careCase) return NotFound();
        if (careCase.CarePlan is not { } plan) return NotFound();

        try
        {
            CarePlanPolicy.RecordGoalProgress(plan, goalId, request.Status, request.Note, Now, Actor);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid goal progress", Detail = ex.Message });
        }

        careCase.LastUpdatedDate = Now;
        careCase.LastUpdatedBy = Actor;

        return await SaveAsync(careCase, ct);
    }

    // ── Assessments and activities ────────────────────────────────

    /// <summary>Records a completed assessment against the case.</summary>
    [HttpPost("{id}/assessments")]
    [ProducesResponseType(typeof(CareCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CareCase>> AddAssessment(
        string id, [FromBody] CareAssessment assessment, CancellationToken ct)
    {
        if (await cases.GetByIdAsync(id, ct) is not { } careCase) return NotFound();

        if (assessment.Status == CareAssessmentStatus.Completed)
        {
            assessment.CompletedAt ??= Now;
            assessment.CompletedBy ??= Actor;
        }

        careCase.Assessments.Add(assessment);
        careCase.LastUpdatedDate = Now;
        careCase.LastUpdatedBy = Actor;

        return await SaveAsync(careCase, ct);
    }

    /// <summary>
    /// Adds an activity and queues it for the care manager. The activity is
    /// recorded on the case whether or not the work queue took it: the care
    /// record does not depend on the queue being up.
    /// </summary>
    [HttpPost("{id}/activities")]
    [ProducesResponseType(typeof(CareCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CareCase>> AddActivity(
        string id, [FromBody] CareActivity activity, CancellationToken ct)
    {
        if (await cases.GetByIdAsync(id, ct) is not { } careCase) return NotFound();

        activity.CreatedDate = Now;
        activity.CreatedBy = Actor;
        activity.AssignedTo ??= careCase.CareManagerId;
        activity.Status = CareActivityStatus.Open;

        activity.WorkItemId = await work.EnqueueAsync(new CareWorkItemCommand
        {
            TenantId   = careCase.TenantId,
            CaseId     = careCase.Id,
            CaseNumber = careCase.CaseNumber,
            MemberId   = careCase.MemberId,
            Activity   = activity,
            Program    = careCase.Program,
            Acuity     = careCase.Acuity,
        }, ct);

        careCase.Activities.Add(activity);
        careCase.LastUpdatedDate = Now;
        careCase.LastUpdatedBy = Actor;

        return await SaveAsync(careCase, ct);
    }

    /// <summary>Completes an activity and closes the work item behind it.</summary>
    [HttpPost("{id}/activities/{activityId}/complete")]
    [ProducesResponseType(typeof(CareCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CareCase>> CompleteActivity(
        string id, string activityId, [FromBody] CompleteActivityRequest request, CancellationToken ct)
    {
        if (await cases.GetByIdAsync(id, ct) is not { } careCase) return NotFound();

        var activity = careCase.Activities.FirstOrDefault(
            a => string.Equals(a.Id, activityId, StringComparison.OrdinalIgnoreCase));
        if (activity is null) return NotFound();

        activity.Status = CareActivityStatus.Completed;
        activity.CompletedAt = Now;
        activity.Outcome = request.Outcome;

        if (activity.WorkItemId is { Length: > 0 } workItemId)
            await work.CompleteAsync(careCase.TenantId, workItemId, request.Outcome, ct);

        careCase.LastUpdatedDate = Now;
        careCase.LastUpdatedBy = Actor;

        return await SaveAsync(careCase, ct);
    }

    private async Task<ActionResult<CareCase>> SaveAsync(CareCase careCase, CancellationToken ct)
    {
        try
        {
            return Ok(await cases.UpdateAsync(careCase, ct));
        }
        catch (CareCaseConcurrencyException ex)
        {
            return Conflict(new ProblemDetails { Title = "Care case changed", Detail = ex.Message });
        }
    }

    internal CareCaseSummary Summarize(CareCase careCase)
    {
        var progress = CarePlanPolicy.Summarize(careCase.CarePlan, Now);
        var name = $"{careCase.MemberLastName}, {careCase.MemberFirstName}".Trim(' ', ',');

        return new CareCaseSummary
        {
            Id                 = careCase.Id,
            CaseNumber         = careCase.CaseNumber,
            MemberId           = careCase.MemberId,
            MemberName         = string.IsNullOrWhiteSpace(name) ? null : name,
            Program            = careCase.Program,
            Status             = careCase.Status,
            Acuity             = careCase.Acuity,
            CareManagerName    = careCase.CareManagerName,
            NextContactDueDate = careCase.NextContactDueDate,
            ContactOverdue     = careCase.Status is not (CareCaseStatus.Closed or CareCaseStatus.Declined)
                                 && careCase.NextContactDueDate?.Date < Now.Date,
            OpenActivities     = careCase.Activities.Count(a => a.Status == CareActivityStatus.Open),
            CarePlanStatus     = careCase.CarePlan?.Status,
            TotalGoals         = progress.TotalGoals,
            AchievedGoals      = progress.AchievedGoals,
            OverdueGoals       = progress.OverdueGoals,
            OpenBarriers       = progress.OpenBarriers,
        };
    }
}

public sealed class CompleteActivityRequest
{
    public string? Outcome { get; set; }
}
