using Microsoft.AspNetCore.Mvc;
using ClaimsService.Middleware;
using ClaimsService.Models;
using ClaimsService.Repositories;

namespace ClaimsService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ClaimsController : ControllerBase
{
    private readonly IClaimRepository _claimRepository;
    private readonly ILogger<ClaimsController> _logger;

    public ClaimsController(
        IClaimRepository claimRepository,
        ILogger<ClaimsController> logger)
    {
        _claimRepository = claimRepository;
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

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
