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
    private readonly IClaimAcknowledgmentService _ackService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClaimsController> _logger;

    public ClaimsController(
        IClaimRepository claimRepository,
        IClaimAcknowledgmentService ackService,
        IConfiguration configuration,
        ILogger<ClaimsController> logger)
    {
        _claimRepository = claimRepository;
        _ackService = ackService;
        _configuration = configuration;
        _logger = logger;
    }

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

        // Categorize by claim-level adjudication denial reason code
        static string? PendCode(Claim c) => c.AdjudicationResult?.DenialReasonCode;

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

        var items = pendedClaims.Select(c => new WorkQueueItem
        {
            ClaimId = c.Id,
            MemberName = c.SubscriberLastName != null ? $"{c.SubscriberFirstName} {c.SubscriberLastName}" : c.MemberId,
            MemberId = c.MemberId,
            ProviderName = c.BillingProviderName ?? c.BillingProviderNPI,
            ServiceDate = c.ClaimLines.FirstOrDefault()?.ServiceDateFrom ?? c.CreatedDate,
            QueueReason = MapPendReason(c.AdjudicationResult?.DenialReasonCode),
            QueueReasonCode = c.AdjudicationResult?.DenialReasonCode ?? "REVIEW",
            DaysInQueue = (int)(DateTime.UtcNow - c.LastUpdatedDate).TotalDays,
            Priority = (DateTime.UtcNow - c.LastUpdatedDate).TotalDays > 14 ? "High" :
                       (DateTime.UtcNow - c.LastUpdatedDate).TotalDays > 7 ? "Medium" : "Low",
            AssignedTo = "",
            TotalCharged = c.TotalChargeAmount,
            ProcedureCodes = c.ClaimLines.Select(sl => sl.ProcedureCode).ToList()
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
}

public class AssignClaimRequest
{
    public string AssignTo { get; set; } = string.Empty;
}

public class OverrideClaimRequest
{
    public string OverrideReason { get; set; } = string.Empty;
}
