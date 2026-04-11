using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;

namespace ClaimsService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ClaimsController : ControllerBase
{
    private readonly IClaimRepository _claimRepository;
    private readonly IAiExaminationAuditRepository _auditRepository;
    private readonly IClaimAcknowledgmentService _ackService;
    private readonly IMpipAdjudicationEnhancer _mpipEnhancer;
    private readonly IClaimEventPublisher _eventPublisher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClaimsController> _logger;

    public ClaimsController(
        IClaimRepository claimRepository,
        IAiExaminationAuditRepository auditRepository,
        IClaimAcknowledgmentService ackService,
        IMpipAdjudicationEnhancer mpipEnhancer,
        IClaimEventPublisher eventPublisher,
        IConfiguration configuration,
        ILogger<ClaimsController> logger)
    {
        _claimRepository = claimRepository;
        _auditRepository = auditRepository;
        _ackService = ackService;
        _mpipEnhancer = mpipEnhancer;
        _eventPublisher = eventPublisher;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Returns the tenant ID or throws if missing. Use on mutation endpoints
    /// where empty tenant would break multi-tenant isolation.
    /// </summary>
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

    /// <summary>
    /// Returns the tenant ID or empty string. Use on read-only endpoints
    /// where degraded operation is acceptable.
    /// </summary>
    private string TryGetTenantId() =>
        HttpContext?.Items["TenantId"]?.ToString() ?? string.Empty;

    /// <summary>
    /// Submit new claim (837 transaction)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Claim>> SubmitClaim([FromBody] Claim claim)
    {
        _logger.LogInformation(
            "Submitting claim for member {MemberId}, provider {ProviderNPI}, service date {ServiceDate}",
            SanitizeForLog(claim.MemberId), SanitizeForLog(claim.BillingProviderNPI), claim.ServiceDateFrom);

        // Validate claim
        if (claim.ClaimLines.Count == 0)
        {
            return BadRequest("Claim must have at least one service line");
        }

        // Calculate total charge (sum of lines)
        claim.TotalChargeAmount = claim.ClaimLines.Sum(l => l.ChargeAmount * l.Units);

        claim.Id = Guid.NewGuid().ToString();
        claim.Status = ClaimStatus.Submitted;
        claim.SubmittedDate = DateTime.UtcNow;
        claim.CreatedDate = DateTime.UtcNow;
        claim.LastUpdatedDate = DateTime.UtcNow;

        var created = await _claimRepository.CreateAsync(claim);

        _logger.LogInformation("Claim {ClaimNumber} submitted successfully", SanitizeForLog(claim.ClaimNumber));

        return CreatedAtAction(nameof(GetClaimById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Get recent claims (for dashboard display)
    /// </summary>
    [HttpGet("recent")]
    [ProducesResponseType(typeof(IEnumerable<Claim>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<Claim>>> GetRecentClaims(
        [FromQuery][Range(1, 100)] int count = 10)
    {
        _logger.LogInformation("Fetching {Count} recent claims", count);

        var claims = await _claimRepository.SearchAsync(
            memberId: null, providerNPI: null,
            serviceDateFrom: null, serviceDateTo: null,
            status: null, lineOfBusiness: null,
            page: 1, pageSize: count);

        return Ok(claims);
    }

    /// <summary>
    /// Get claim by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Claim>> GetClaimById(string id)
    {
        _logger.LogInformation("Fetching claim by ID: {Id}", SanitizeForLog(id));

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        return Ok(claim);
    }

    /// <summary>
    /// Get claim by claim number
    /// </summary>
    [HttpGet("number/{claimNumber}")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Claim>> GetClaimByNumber(string claimNumber)
    {
        _logger.LogInformation("Fetching claim by number: {ClaimNumber}", SanitizeForLog(claimNumber));

        var claim = await _claimRepository.GetByClaimNumberAsync(claimNumber);
        if (claim == null)
        {
            return NotFound($"Claim {claimNumber} not found");
        }

        return Ok(claim);
    }

    /// <summary>
    /// Search claims (by member, provider, date range, status)
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(IEnumerable<Claim>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Claim>>> SearchClaims(
        [FromQuery] string? memberId = null,
        [FromQuery] string? providerNPI = null,
        [FromQuery] DateTime? serviceDateFrom = null,
        [FromQuery] DateTime? serviceDateTo = null,
        [FromQuery] ClaimStatus? status = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation(
            "Searching claims: member={Member}, provider={Provider}, dateFrom={From}, dateTo={To}, status={Status}, lob={LOB}",
            SanitizeForLog(memberId), SanitizeForLog(providerNPI), serviceDateFrom, serviceDateTo, status, lineOfBusiness);

        var claims = await _claimRepository.SearchAsync(
            memberId, providerNPI, serviceDateFrom, serviceDateTo, status, lineOfBusiness, page, pageSize);

        return Ok(claims);
    }

    /// <summary>Search claims via POST body (portal search form).</summary>
    [HttpPost("search")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchClaimsPost([FromBody] ClaimSearchBody body)
    {
        ClaimStatus? status = null;
        if (!string.IsNullOrEmpty(body.Status) && Enum.TryParse<ClaimStatus>(body.Status, true, out var parsed))
            status = parsed;

        var claims = await _claimRepository.SearchAsync(
            body.MemberId, body.ProviderId,
            body.ServiceDateFrom, body.ServiceDateTo,
            status, null,
            body.PageNumber, body.PageSize);

        var list = claims.ToList();
        return Ok(new { claims = list, totalCount = list.Count, page = body.PageNumber, pageSize = body.PageSize });
    }

    /// <summary>
    /// Update claim status (277 claim status update)
    /// </summary>
    [HttpPut("{id}/status")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Claim>> UpdateClaimStatus(
        string id,
        [FromBody] ClaimStatusUpdate statusUpdate)
    {
        _logger.LogInformation(
            "Updating claim {Id} status to {Status}",
            SanitizeForLog(id), statusUpdate.Status);

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        claim.Status = statusUpdate.Status;
        claim.LastUpdatedDate = DateTime.UtcNow;

        // Set dates based on status
        switch (statusUpdate.Status)
        {
            case ClaimStatus.Received:
                claim.ReceivedDate = DateTime.UtcNow;
                break;
            case ClaimStatus.Approved:
            case ClaimStatus.Denied:
            case ClaimStatus.PartiallyPaid:
                claim.AdjudicatedDate = DateTime.UtcNow;
                break;
            case ClaimStatus.Paid:
                claim.PaidDate = DateTime.UtcNow;
                break;
        }

        if (!string.IsNullOrEmpty(statusUpdate.Notes))
        {
            claim.ClaimNotes = string.IsNullOrEmpty(claim.ClaimNotes)
                ? statusUpdate.Notes
                : $"{claim.ClaimNotes}\n{DateTime.UtcNow:yyyy-MM-dd HH:mm}: {statusUpdate.Notes}";
        }

        var updated = await _claimRepository.UpdateAsync(claim);

        // If a generic status update happens to land on Pended without going through
        // /pend, still publish the event so downstream consumers see the transition.
        // Payload will lack PendDetails — consumers must tolerate that.
        if (updated.Status == ClaimStatus.Pended)
        {
            await _eventPublisher.PublishClaimPendedAsync(updated, GetTenantId());
        }

        return Ok(updated);
    }

    /// <summary>
    /// Pend a claim with structured pend details and emit a ClaimPendedEvent.
    ///
    /// This is the primary entry point used by the Argo adjudication workflow when
    /// a deterministic edit (NCCI/MUE today, others later) requires human review.
    /// The single call sets Status=Pended, writes PendDetails, and publishes the
    /// event in one transition so the downstream AI examiner service has all the
    /// context it needs without an extra round-trip.
    ///
    /// PendDetails is the authoritative deterministic record of why the claim
    /// pended. The AI examiner writes its advisory output to AiExamination via
    /// PUT /{id}/ai-examination — never here.
    /// </summary>
    [HttpPut("{id}/pend")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Claim>> PendClaim(string id, [FromBody] PendDetails pendDetails)
    {
        if (string.IsNullOrWhiteSpace(pendDetails.PendCode))
        {
            return BadRequest("pendCode is required");
        }

        _logger.LogInformation(
            "Pending claim {Id} with code {PendCode} ({EditCount} edit failures)",
            SanitizeForLog(id), SanitizeForLog(pendDetails.PendCode), pendDetails.EditFailures.Count);

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        if (pendDetails.PendedAt == default)
        {
            pendDetails.PendedAt = DateTime.UtcNow;
        }

        claim.PendDetails = pendDetails;
        claim.Status = ClaimStatus.Pended;
        claim.LastUpdatedDate = DateTime.UtcNow;

        var updated = await _claimRepository.UpdateAsync(claim);

        await _eventPublisher.PublishClaimPendedAsync(updated, GetTenantId());

        return Ok(updated);
    }

    /// <summary>
    /// Update claim with adjudication results (from adjudication workflow)
    /// </summary>
    [HttpPut("{id}/adjudication")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Claim>> UpdateAdjudication(
        string id,
        [FromBody] AdjudicationResult adjudication)
    {
        _logger.LogInformation(
            "Updating claim {Id} with adjudication: allowed={Allowed}, payer={Payer}, patient={Patient}",
            SanitizeForLog(id), adjudication.AllowedAmount, adjudication.PayerPayment, adjudication.PatientResponsibility);

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        claim.AdjudicationResult = adjudication;
        claim.AdjudicatedDate = DateTime.UtcNow;
        claim.LastUpdatedDate = DateTime.UtcNow;

        // Apply FL SMMC 3.0 MPIP enhanced rate if applicable.
        // Must run after base allowed amount is set and before status determination.
        if (claim.LineOfBusiness == LineOfBusiness.Medicaid)
        {
            var memberAge = CalculateMemberAge(claim);
            if (memberAge.HasValue)
            {
                await _mpipEnhancer.ApplyMpipEnhancementAsync(claim, memberAge.Value);
            }
        }

        // Update status based on adjudication
        if (adjudication.PayerPayment == 0 && !string.IsNullOrEmpty(adjudication.DenialReasonCode))
        {
            claim.Status = ClaimStatus.Denied;
        }
        else if (adjudication.PayerPayment > 0)
        {
            claim.Status = ClaimStatus.Approved;
        }

        var updated = await _claimRepository.UpdateAsync(claim);
        return Ok(updated);
    }

    /// <summary>
    /// Write the AI Claims Examiner's advisory recommendation onto a pended claim.
    ///
    /// Called by claims-examiner-service after it processes a ClaimPendedEvent.
    /// Strictly advisory: this never changes Status, never touches AdjudicationResult,
    /// never touches PendDetails. It only writes Claim.AiExamination so the work queue
    /// can show the recommendation alongside the claim. A human examiner remains in
    /// the loop and must call /work-queue/{id}/override or /work-queue/{id}/assign
    /// to actually act on the claim.
    ///
    /// Idempotent on retry: a second call from the examiner service for the same
    /// claim simply replaces the prior recommendation. The ExaminerAgreement field
    /// is preserved across overwrites so we don't lose feedback signal if the model
    /// re-examines a claim a human has already acted on.
    /// </summary>
    [HttpPut("{id}/ai-examination")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Claim>> SetAiExamination(string id, [FromBody] AiExamination examination)
    {
        if (string.IsNullOrWhiteSpace(examination.RecommendedDisposition))
        {
            return BadRequest("recommendedDisposition is required");
        }

        var validDispositions = new[] { "Approve", "Deny", "RequestInfo", "EscalateToHuman" };
        if (!validDispositions.Contains(examination.RecommendedDisposition))
        {
            return BadRequest($"recommendedDisposition must be one of: {string.Join(", ", validDispositions)}");
        }

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        // Reject AI recommendations on claims that are no longer pended. The model
        // is only meaningful while the claim is awaiting human review; if the claim
        // has already been approved, denied, or paid, a stale recommendation could
        // confuse the work queue display.
        if (claim.Status != ClaimStatus.Pended)
        {
            _logger.LogWarning(
                "Rejected AI examination for claim {Id}: status is {Status}, not Pended",
                SanitizeForLog(id), claim.Status);
            return Conflict($"Claim {id} is in status {claim.Status}, not Pended; AI examination ignored");
        }

        if (examination.GeneratedAt == default)
        {
            examination.GeneratedAt = DateTime.UtcNow;
        }

        // Preserve any prior examiner-agreement signal across re-examinations,
        // so we don't lose human feedback if the examiner service re-runs the model.
        if (claim.AiExamination?.ExaminerAgreement is { } prior)
        {
            examination.ExaminerAgreement = prior;
            examination.ExaminerActedAt = claim.AiExamination.ExaminerActedAt;
            examination.ExaminerUserId = claim.AiExamination.ExaminerUserId;
        }

        claim.AiExamination = examination;
        claim.LastUpdatedDate = DateTime.UtcNow;

        var updated = await _claimRepository.UpdateAsync(claim);

        // Append an immutable audit row for this recommendation. Snapshots
        // pend-detail context (rule id, code pair) so historical analyses can
        // correlate model accuracy with the specific deterministic edit type
        // even if the claim is later re-pended for a different reason.
        var firstEdit = claim.PendDetails?.EditFailures.FirstOrDefault();
        var audit = new AiExaminationAudit
        {
            TenantId = GetTenantId(),
            ClaimId = id,
            PendCode = claim.PendDetails?.PendCode,
            RuleId = firstEdit?.RuleId,
            Column1Code = firstEdit?.Column1Code,
            Column2Code = firstEdit?.Column2Code,
            RecommendedDisposition = examination.RecommendedDisposition,
            ConfidenceScore = examination.ConfidenceScore,
            Rationale = examination.Rationale,
            PolicyCitations = new List<string>(examination.PolicyCitations),
            ModelId = examination.ModelId,
            PromptVersion = examination.PromptVersion,
            GeneratedAt = examination.GeneratedAt
        };

        try
        {
            await _auditRepository.AppendAsync(audit);
        }
        catch (Exception ex)
        {
            // Audit-append failures must not break the live recommendation write.
            // The Claim.AiExamination update has already succeeded above; the
            // audit collection is the long-term record but the work-queue UI
            // can still function without the latest audit row.
            _logger.LogError(ex,
                "Failed to append AI examination audit for claim {Id}; live recommendation persisted, audit lost",
                SanitizeForLog(id));
        }

        _logger.LogInformation(
            "AI examination set on claim {Id}: disposition={Disposition} confidence={Confidence:F2} model={Model} prompt={Prompt}",
            SanitizeForLog(id),
            examination.RecommendedDisposition,
            examination.ConfidenceScore,
            SanitizeForLog(examination.ModelId),
            SanitizeForLog(examination.PromptVersion));

        return Ok(updated);
    }

    /// <summary>
    /// Get the full audit history of AI Claims Examiner recommendations for a claim,
    /// newest first. Each row is the immutable record of one model run; the live
    /// "current" recommendation is on Claim.AiExamination.
    ///
    /// This endpoint powers the work-queue UI's "show prior AI runs" expander and
    /// the 90-day override-rate analysis pipeline (joined on PromptVersion x
    /// RecommendedDisposition x ExaminerAgreement).
    /// </summary>
    [HttpGet("{id}/ai-examination/audit")]
    [ProducesResponseType(typeof(IEnumerable<AiExaminationAudit>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<AiExaminationAudit>>> GetAiExaminationAudit(
        string id, CancellationToken ct = default)
    {
        var history = await _auditRepository.GetByClaimAsync(id, TryGetTenantId(), ct);
        return Ok(history);
    }

    /// <summary>
    /// Record an examiner's agreement (or disagreement) with a prior AI recommendation.
    /// This is the feedback signal the 90-day override-rate analysis depends on.
    /// Call this from the work-queue UI whenever an examiner acts on a claim that
    /// has an AI recommendation attached.
    ///
    /// Values for `agreement`: Accepted | Modified | Overridden.
    /// </summary>
    [HttpPost("{id}/ai-examination/agreement")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Claim>> SetAiExaminerAgreement(
        string id,
        [FromBody] AiExaminerAgreementRequest request)
    {
        var validAgreements = new[] { "Accepted", "Modified", "Overridden" };
        if (string.IsNullOrWhiteSpace(request.Agreement) || !validAgreements.Contains(request.Agreement))
        {
            return BadRequest($"agreement must be one of: {string.Join(", ", validAgreements)}");
        }

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null) return NotFound($"Claim {id} not found");

        if (claim.AiExamination == null)
        {
            return BadRequest($"Claim {id} has no AI examination to mark agreement on");
        }

        claim.AiExamination.ExaminerAgreement = request.Agreement;
        claim.AiExamination.ExaminerActedAt = DateTime.UtcNow;
        claim.AiExamination.ExaminerUserId = request.ExaminerUserId;
        claim.LastUpdatedDate = DateTime.UtcNow;

        var updated = await _claimRepository.UpdateAsync(claim);

        // Cascade the agreement to the latest audit row. This is the only mutation
        // permitted on an audit record and is enforced single-write at the repository.
        // A null return means there was no audit row to update (recommendation may
        // have been written before the audit collection existed) — log and continue.
        var auditUpdated = await _auditRepository.SetExaminerAgreementAsync(
            id, GetTenantId(), request.Agreement, request.ExaminerUserId, request.Notes);

        if (auditUpdated is null)
        {
            _logger.LogWarning(
                "No audit row found for claim {Id} when setting examiner agreement; live AiExamination updated only",
                SanitizeForLog(id));
        }

        _logger.LogInformation(
            "Examiner {User} marked claim {Id} as {Agreement} (recommended {Disposition})",
            SanitizeForLog(request.ExaminerUserId), SanitizeForLog(id),
            request.Agreement, claim.AiExamination.RecommendedDisposition);

        return Ok(updated);
    }

    /// <summary>
    /// Process 835 remittance (payment/denial notification)
    /// </summary>
    [HttpPost("{id}/remittance")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Claim>> ProcessRemittance(
        string id,
        [FromBody] RemittanceUpdate remittance)
    {
        _logger.LogInformation(
            "Processing remittance for claim {Id}, check number {CheckNumber}",
            SanitizeForLog(id), SanitizeForLog(remittance.CheckNumber));

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        claim.EDI835ControlNumber = remittance.ControlNumber;
        claim.PaidDate = DateTime.UtcNow;
        claim.Status = remittance.PaymentAmount > 0 ? ClaimStatus.Paid : ClaimStatus.Denied;
        claim.LastUpdatedDate = DateTime.UtcNow;

        // Update adjudication if not already set
        if (claim.AdjudicationResult == null)
        {
            claim.AdjudicationResult = new AdjudicationResult();
        }

        claim.AdjudicationResult.CheckNumber = remittance.CheckNumber;
        claim.AdjudicationResult.PaymentDate = remittance.PaymentDate;
        claim.AdjudicationResult.PayerPayment = remittance.PaymentAmount;

        var updated = await _claimRepository.UpdateAsync(claim);
        return Ok(updated);
    }

    /// <summary>
    /// Get claims summary statistics
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(ClaimsSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<ClaimsSummary>> GetClaimsSummary(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null)
    {
        var fromDate = from ?? DateTime.UtcNow.AddMonths(-1);
        var toDate = to ?? DateTime.UtcNow;

        _logger.LogInformation(
            "Fetching claims summary from {From} to {To}, lob={LOB}",
            fromDate, toDate, lineOfBusiness);

        var summary = await _claimRepository.GetClaimsSummaryAsync(fromDate, toDate, lineOfBusiness);
        return Ok(summary);
    }

    /// <summary>
    /// Download the X12 277CA Claim Acknowledgment for a claim
    /// </summary>
    [HttpGet("{id}/277ca")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetClaimAcknowledgment(string id)
    {
        var claim = await _claimRepository.GetByIdAsync(id);

        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        var cfg = new ClaimAcknowledgmentConfig
        {
            InterchangeSenderId   = _configuration["Ack:InterchangeSenderId"]   ?? "CHO",
            InterchangeReceiverId = _configuration["Ack:InterchangeReceiverId"] ?? "RECEIVER",
            ApplicationSenderId   = _configuration["Ack:ApplicationSenderId"]   ?? "CHO",
            ApplicationReceiverId = _configuration["Ack:ApplicationReceiverId"] ?? "RECEIVER",
            PayerName             = _configuration["Ack:PayerName"]             ?? "Cloud Health Office",
            PayerId               = _configuration["Ack:PayerId"]               ?? "CHO",
            PayerOriginatorId     = _configuration["Ack:PayerOriginatorId"]     ?? "CHO",
        };

        _logger.LogInformation(
            "Generating 277CA for claim {ClaimId} ({ClaimNumber}), status={Status}",
            SanitizeForLog(id), SanitizeForLog(claim.ClaimNumber), claim.Status);

        var edi = _ackService.Generate277CA(claim, cfg);

        var filename = $"277CA_{claim.ClaimNumber}.edi";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";
        return Content(edi, "text/plain");
    }

    /// <summary>
    /// Delete claim (soft delete - set status to Voided)
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VoidClaim(string id)
    {
        _logger.LogInformation("Voiding claim: {Id}", SanitizeForLog(id));

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        claim.Status = ClaimStatus.Voided;
        claim.LastUpdatedDate = DateTime.UtcNow;

        await _claimRepository.UpdateAsync(claim);

        return NoContent();
    }

    /// <summary>
    /// Get aggregated accumulator totals for a member or family for a plan year.
    ///
    /// Called by the Redis accumulator service on a cache miss to rebuild from claim
    /// history. Returns the sum of deductible, OOP, coinsurance, and copay amounts
    /// across all finalized claims for the given owner / plan / year combination.
    ///
    /// <list type="bullet">
    ///   <item><paramref name="ownerId"/> — memberId for Individual scope; subscriberId for Family scope.</item>
    ///   <item><paramref name="scope"/> — "Individual" or "Family".</item>
    ///   <item><paramref name="planYear"/> — four-digit year string, e.g. "2026".</item>
    /// </list>
    /// </summary>
    [HttpGet("accumulator-totals")]
    [ProducesResponseType(typeof(AccumulatorTotalsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AccumulatorTotalsResponse>> GetAccumulatorTotals(
        [FromQuery] string ownerId,
        [FromQuery] string scope,
        [FromQuery] string benefitPlanId,
        [FromQuery] string planYear,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return BadRequest("ownerId is required");
        if (scope != "Individual" && scope != "Family")
            return BadRequest("scope must be 'Individual' or 'Family'");
        if (string.IsNullOrWhiteSpace(benefitPlanId))
            return BadRequest("benefitPlanId is required");
        if (!int.TryParse(planYear, out _))
            return BadRequest("planYear must be a four-digit year, e.g. '2026'");

        _logger.LogDebug(
            "Accumulator totals request: owner={OwnerId}, scope={Scope}, plan={PlanId}, year={Year}",
            SanitizeForLog(ownerId), scope, SanitizeForLog(benefitPlanId), planYear);

        var result = await _claimRepository.GetAccumulatorTotalsAsync(
            ownerId, scope, benefitPlanId, planYear, ct);

        return Ok(result);
    }

    // ── Work Queue endpoints ────────────────────────────────────────────
    // These power the portal's Claims Work Queue page. Work queue items
    // are derived from claims in Pended status.

    /// <summary>
    /// Get work queue summary counts by pend reason
    /// </summary>
    [HttpGet("work-queue/summary")]
    [ProducesResponseType(typeof(WorkQueueSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<WorkQueueSummary>> GetWorkQueueSummary()
    {
        var pendedClaims = (await _claimRepository.SearchAsync(
            memberId: null, providerNPI: null,
            serviceDateFrom: null, serviceDateTo: null,
            status: ClaimStatus.Pended, lineOfBusiness: null,
            page: 1, pageSize: 1000)).ToList();

        // Prefer PendDetails (structured pend reason from workflow); fall back to
        // legacy AdjudicationResult.DenialReasonCode for pre-PendDetails claims.
        static string? PendCode(Claim c) =>
            c.PendDetails?.PendCode ?? c.AdjudicationResult?.DenialReasonCode;

        var summary = new WorkQueueSummary
        {
            NcciEditFailures = pendedClaims.Count(c => PendCode(c) is "NCCI" or "MUE"),
            MissingAuth = pendedClaims.Count(c => PendCode(c) is "AUTH" or "NOAUTH"),
            ProviderNotContracted = pendedClaims.Count(c => PendCode(c) is "OON" or "NOCONTRACT"),
            CobRequired = pendedClaims.Count(c => PendCode(c) is "COB"),
            MedicalReview = pendedClaims.Count(c => PendCode(c) is "MEDREVIEW" or "CLINICAL")
        };

        // Claims without a recognized pend reason go to medical review as default
        var categorized = summary.NcciEditFailures + summary.MissingAuth +
                          summary.ProviderNotContracted + summary.CobRequired + summary.MedicalReview;
        summary.MedicalReview += pendedClaims.Count - categorized;

        return Ok(summary);
    }

    /// <summary>
    /// Get work queue items (pended claims for examiner review)
    /// </summary>
    [HttpGet("work-queue/items")]
    [ProducesResponseType(typeof(IEnumerable<WorkQueueItem>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<WorkQueueItem>>> GetWorkQueueItems(
        [FromQuery] string? queueType = null,
        [FromQuery] string? assignedTo = null,
        [FromQuery] int limit = 100)
    {
        var pendedClaims = (await _claimRepository.SearchAsync(
            memberId: null, providerNPI: null,
            serviceDateFrom: null, serviceDateTo: null,
            status: ClaimStatus.Pended, lineOfBusiness: null,
            page: 1, pageSize: limit)).ToList();

        var items = pendedClaims.Select(c =>
        {
            // Prefer the structured PendDetails written by the workflow's /pend call;
            // fall back to legacy AdjudicationResult.DenialReasonCode for claims that
            // pre-date the PendDetails field.
            var pendCode = c.PendDetails?.PendCode ?? c.AdjudicationResult?.DenialReasonCode;
            return new WorkQueueItem
            {
                ClaimId = c.Id,
                MemberName = c.SubscriberLastName != null ? $"{c.SubscriberFirstName} {c.SubscriberLastName}" : c.MemberId,
                MemberId = c.MemberId,
                ProviderName = c.BillingProviderName ?? c.BillingProviderNPI,
                ServiceDate = c.ClaimLines.FirstOrDefault()?.ServiceDateFrom ?? c.CreatedDate,
                QueueReason = MapPendReason(pendCode),
                QueueReasonCode = pendCode ?? "REVIEW",
                DaysInQueue = (int)(DateTime.UtcNow - c.LastUpdatedDate).TotalDays,
                Priority = (DateTime.UtcNow - c.LastUpdatedDate).TotalDays > 14 ? "High" :
                           (DateTime.UtcNow - c.LastUpdatedDate).TotalDays > 7 ? "Medium" : "Low",
                AssignedTo = "",
                TotalCharged = c.TotalChargeAmount,
                ProcedureCodes = c.ClaimLines.Select(sl => sl.ProcedureCode).ToList(),
                AiRecommendedDisposition = c.AiExamination?.RecommendedDisposition,
                AiConfidenceScore = c.AiExamination?.ConfidenceScore,
                AiRationale = c.AiExamination?.Rationale,
                AiPolicyCitations = c.AiExamination?.PolicyCitations ?? new List<string>(),
                AiExaminerAgreement = c.AiExamination?.ExaminerAgreement
            };
        }).ToList();

        if (!string.IsNullOrEmpty(queueType))
            items = items.Where(i => i.QueueReasonCode == queueType).ToList();

        return Ok(items);
    }

    /// <summary>
    /// Assign a pended claim to an examiner
    /// </summary>
    [HttpPost("work-queue/{claimId}/assign")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AssignClaim(string claimId, [FromBody] AssignClaimRequest request)
    {
        var claim = await _claimRepository.GetByIdAsync(claimId);
        if (claim == null) return NotFound();

        // In a full implementation, this would update an AssignedTo field.
        // For now, just log and return success.
        _logger.LogInformation("Claim {ClaimId} assigned to {AssignedTo}", SanitizeForLog(claimId), SanitizeForLog(request.AssignTo));
        return Ok(new { claimId, assignedTo = request.AssignTo });
    }

    /// <summary>
    /// Override a pended claim (supervisor action)
    /// </summary>
    [HttpPost("work-queue/{claimId}/override")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> OverrideClaim(string claimId, [FromBody] OverrideClaimRequest request)
    {
        var claim = await _claimRepository.GetByIdAsync(claimId);
        if (claim == null) return NotFound();

        claim.Status = ClaimStatus.Approved;
        claim.LastUpdatedDate = DateTime.UtcNow;
        await _claimRepository.UpdateAsync(claim);

        _logger.LogInformation("Claim {ClaimId} override-approved: {Reason}", SanitizeForLog(claimId), SanitizeForLog(request.OverrideReason));
        return Ok(new { claimId, status = "Approved", overrideReason = request.OverrideReason });
    }

    private static string MapPendReason(string? code) => code switch
    {
        "NCCI" or "MUE" => "NCCI Edit Failure",
        "AUTH" or "NOAUTH" => "Missing Authorization",
        "OON" or "NOCONTRACT" => "Provider Not Contracted",
        "COB" => "COB Required",
        "MEDREVIEW" or "CLINICAL" => "Medical Review",
        _ => "Pending Review"
    };

    /// <summary>
    /// Estimate member age at the service date from claim data.
    /// Returns null when DOB is unavailable so callers can skip MPIP.
    /// </summary>
    private static int? CalculateMemberAge(Claim claim)
    {
        // Member DOB is not on the Claim model; in a full implementation
        // it would be fetched from the member-service. Return null so
        // callers skip MPIP when age is unknown.
        return null;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class WorkQueueSummary
{
    public int NcciEditFailures { get; set; }
    public int MissingAuth { get; set; }
    public int ProviderNotContracted { get; set; }
    public int CobRequired { get; set; }
    public int MedicalReview { get; set; }
}

public class WorkQueueItem
{
    public string ClaimId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTime ServiceDate { get; set; }
    public string QueueReason { get; set; } = string.Empty;
    public string QueueReasonCode { get; set; } = string.Empty;
    public int DaysInQueue { get; set; }
    public string Priority { get; set; } = "Low";
    public string AssignedTo { get; set; } = string.Empty;
    public decimal TotalCharged { get; set; }
    public List<string> ProcedureCodes { get; set; } = new();

    /// <summary>AI examiner's recommended disposition, null if no AI run yet.</summary>
    public string? AiRecommendedDisposition { get; set; }

    /// <summary>AI examiner's self-reported confidence (0–1).</summary>
    public double? AiConfidenceScore { get; set; }

    /// <summary>Plain-English rationale shown alongside the claim in the work queue UI.</summary>
    public string? AiRationale { get; set; }

    /// <summary>Policy/rule citations the model relied on.</summary>
    public List<string> AiPolicyCitations { get; set; } = new();

    /// <summary>If a human has acted on this claim: Accepted, Modified, or Overridden.</summary>
    public string? AiExaminerAgreement { get; set; }
}

public class AssignClaimRequest
{
    public string AssignTo { get; set; } = string.Empty;
}

public class OverrideClaimRequest
{
    public string OverrideReason { get; set; } = string.Empty;
}

public class AiExaminerAgreementRequest
{
    /// <summary>Accepted | Modified | Overridden.</summary>
    public string Agreement { get; set; } = string.Empty;

    /// <summary>User who acted on the claim.</summary>
    public string ExaminerUserId { get; set; } = string.Empty;

    /// <summary>Optional free-text note (e.g., why the examiner overrode the AI).</summary>
    public string? Notes { get; set; }
}
