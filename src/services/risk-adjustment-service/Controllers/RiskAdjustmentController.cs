using Microsoft.AspNetCore.Mvc;
using RiskAdjustmentService.Models;
using RiskAdjustmentService.Repositories;

namespace RiskAdjustmentService.Controllers;

[ApiController]
[Route("api/risk-adjustment")]
[Produces("application/json")]
public class RiskAdjustmentController : ControllerBase
{
    private readonly IRiskScoreRepository _riskScoreRepository;
    private readonly ILogger<RiskAdjustmentController> _logger;

    public RiskAdjustmentController(
        IRiskScoreRepository riskScoreRepository,
        ILogger<RiskAdjustmentController> logger)
    {
        _riskScoreRepository = riskScoreRepository;
        _logger = logger;
    }

    // ── Per-Member Score Endpoints ────────────────────────────────────

    /// <summary>
    /// Get risk score for a specific member and measurement year.
    /// Returns the composite RAF score along with HCC category breakdown.
    /// </summary>
    [HttpGet("members/{memberId}/scores/{measurementYear}")]
    [ProducesResponseType(typeof(MemberRiskScore), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MemberRiskScore>> GetMemberScore(
        string memberId,
        int measurementYear)
    {
        _logger.LogInformation("Fetching risk score for member {MemberId}, year {Year}",
            SanitizeForLog(memberId), measurementYear);

        var score = await _riskScoreRepository.GetByMemberAndYearAsync(memberId, measurementYear);
        if (score == null)
            return NotFound($"No risk score found for member {memberId} in year {measurementYear}");

        return Ok(score);
    }

    /// <summary>
    /// Get the per-member score summary (lighter response without full HCC detail).
    /// </summary>
    [HttpGet("members/{memberId}/scores/{measurementYear}/summary")]
    [ProducesResponseType(typeof(MemberScoreResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MemberScoreResponse>> GetMemberScoreSummary(
        string memberId,
        int measurementYear)
    {
        _logger.LogInformation("Fetching risk score summary for member {MemberId}, year {Year}",
            SanitizeForLog(memberId), measurementYear);

        var score = await _riskScoreRepository.GetByMemberAndYearAsync(memberId, measurementYear);
        if (score == null)
            return NotFound($"No risk score found for member {memberId} in year {measurementYear}");

        var response = new MemberScoreResponse
        {
            MemberId = score.MemberId,
            MemberFirstName = score.MemberFirstName,
            MemberLastName = score.MemberLastName,
            MeasurementYear = score.MeasurementYear,
            RiskModel = score.RiskModel,
            ModelVersion = score.ModelVersion,
            LineOfBusiness = score.LineOfBusiness,
            RiskScore = score.RiskScore,
            DemographicFactor = score.DemographicFactor,
            HccFactor = score.HccFactor,
            InteractionFactor = score.InteractionFactor,
            HccCategoryCount = score.HccCategories.Count(c => !c.IsSuperseded),
            DiagnosisCount = score.Diagnoses.Count,
            Status = score.Status,
            CalculatedDate = score.CalculatedDate,
            IsSubmitted = score.IsSubmitted
        };

        return Ok(response);
    }

    /// <summary>
    /// Get all risk scores for a member across all measurement years (trend).
    /// </summary>
    [HttpGet("members/{memberId}/scores")]
    [ProducesResponseType(typeof(MemberScoreTrend), StatusCodes.Status200OK)]
    public async Task<ActionResult<MemberScoreTrend>> GetMemberScoreTrend(string memberId)
    {
        _logger.LogInformation("Fetching risk score trend for member {MemberId}",
            SanitizeForLog(memberId));

        var scores = await _riskScoreRepository.GetByMemberAsync(memberId);
        var scoreList = scores.ToList();

        var trend = new MemberScoreTrend
        {
            MemberId = memberId,
            MemberFirstName = scoreList.FirstOrDefault()?.MemberFirstName,
            MemberLastName = scoreList.FirstOrDefault()?.MemberLastName,
            YearlyScores = scoreList.Select(s => new YearlyScore
            {
                MeasurementYear = s.MeasurementYear,
                RiskScore = s.RiskScore,
                DemographicFactor = s.DemographicFactor,
                HccFactor = s.HccFactor,
                HccCategoryCount = s.HccCategories.Count(c => !c.IsSuperseded),
                Status = s.Status
            }).ToList()
        };

        return Ok(trend);
    }

    /// <summary>
    /// Create or update a member's risk score for a measurement year.
    /// </summary>
    [HttpPut("members/{memberId}/scores/{measurementYear}")]
    [ProducesResponseType(typeof(MemberRiskScore), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MemberRiskScore), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MemberRiskScore>> UpsertMemberScore(
        string memberId,
        int measurementYear,
        [FromBody] MemberRiskScore score)
    {
        _logger.LogInformation("Upserting risk score for member {MemberId}, year {Year}, score={Score}",
            SanitizeForLog(memberId), measurementYear, score.RiskScore);

        if (score.MemberId != memberId || score.MeasurementYear != measurementYear)
            return BadRequest("Member ID and measurement year in URL must match the request body");

        if (score.HccCategories.Count == 0 && score.RiskScore == 0 && score.DemographicFactor == 0)
            return BadRequest("Risk score must have at least a demographic factor or HCC categories");

        var existing = await _riskScoreRepository.GetByMemberAndYearAsync(memberId, measurementYear);

        if (existing != null)
        {
            score.Id = existing.Id;
            score.CreatedDate = existing.CreatedDate;
            score.CreatedBy = existing.CreatedBy;
            score.LastUpdatedDate = DateTime.UtcNow;

            var updated = await _riskScoreRepository.UpdateAsync(score);
            return Ok(updated);
        }

        score.Id = Guid.NewGuid().ToString();
        score.CreatedDate = DateTime.UtcNow;
        score.LastUpdatedDate = DateTime.UtcNow;

        var created = await _riskScoreRepository.CreateAsync(score);
        return CreatedAtAction(
            nameof(GetMemberScore),
            new { memberId, measurementYear },
            created);
    }

    /// <summary>
    /// Request a score calculation for a member.
    /// In a full implementation this would trigger the HCC scoring engine;
    /// here it creates a placeholder score record with Calculated status.
    /// </summary>
    [HttpPost("scores/calculate")]
    [ProducesResponseType(typeof(MemberRiskScore), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MemberRiskScore>> RequestScoreCalculation(
        [FromBody] ScoreCalculationRequest request)
    {
        _logger.LogInformation(
            "Score calculation requested for member {MemberId}, year {Year}, model {Model}",
            SanitizeForLog(request.MemberId), request.MeasurementYear, request.RiskModel);

        var existing = await _riskScoreRepository.GetByMemberAndYearAsync(
            request.MemberId, request.MeasurementYear);

        if (existing != null)
        {
            existing.Status = ScoreStatus.Calculated;
            existing.CalculatedDate = DateTime.UtcNow;
            existing.LastUpdatedDate = DateTime.UtcNow;
            existing.RiskModel = request.RiskModel;
            existing.ModelVersion = request.ModelVersion;
            existing.LineOfBusiness = request.LineOfBusiness;

            var updated = await _riskScoreRepository.UpdateAsync(existing);
            return Ok(updated);
        }

        var score = new MemberRiskScore
        {
            Id = Guid.NewGuid().ToString(),
            MemberId = request.MemberId,
            MeasurementYear = request.MeasurementYear,
            RiskModel = request.RiskModel,
            ModelVersion = request.ModelVersion,
            LineOfBusiness = request.LineOfBusiness,
            Status = ScoreStatus.Calculated,
            CalculatedDate = DateTime.UtcNow,
            CreatedDate = DateTime.UtcNow,
            LastUpdatedDate = DateTime.UtcNow
        };

        var created = await _riskScoreRepository.CreateAsync(score);
        return Ok(created);
    }

    // ── Measurement Year Data ─────────────────────────────────────────

    /// <summary>
    /// Get all risk scores for a measurement year (paginated).
    /// Returns per-member scores ordered by risk score descending.
    /// </summary>
    [HttpGet("measurement-years/{measurementYear}/scores")]
    [ProducesResponseType(typeof(IEnumerable<MemberRiskScore>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<MemberRiskScore>>> GetMeasurementYearScores(
        int measurementYear,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation("Fetching measurement year {Year} scores, lob={LOB}, page={Page}",
            measurementYear, lineOfBusiness, page);

        var scores = await _riskScoreRepository.GetByMeasurementYearAsync(
            measurementYear, lineOfBusiness, page, pageSize);

        return Ok(scores);
    }

    /// <summary>
    /// Get summary statistics for a measurement year.
    /// Returns aggregate metrics: average/min/max RAF, member counts, top HCC categories.
    /// </summary>
    [HttpGet("measurement-years/{measurementYear}/summary")]
    [ProducesResponseType(typeof(MeasurementYearSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<MeasurementYearSummary>> GetMeasurementYearSummary(
        int measurementYear,
        [FromQuery] LineOfBusiness? lineOfBusiness = null)
    {
        _logger.LogInformation("Fetching measurement year {Year} summary, lob={LOB}",
            measurementYear, lineOfBusiness);

        var summary = await _riskScoreRepository.GetMeasurementYearSummaryAsync(
            measurementYear, lineOfBusiness);

        return Ok(summary);
    }

    // ── Search & Batch Operations ─────────────────────────────────────

    /// <summary>
    /// Search risk scores across members with filters.
    /// </summary>
    [HttpGet("scores/search")]
    [ProducesResponseType(typeof(IEnumerable<MemberRiskScore>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<MemberRiskScore>>> SearchScores(
        [FromQuery] int? measurementYear = null,
        [FromQuery] string? memberId = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] ScoreStatus? status = null,
        [FromQuery] decimal? minScore = null,
        [FromQuery] decimal? maxScore = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation(
            "Searching risk scores: year={Year}, member={Member}, lob={LOB}, status={Status}",
            measurementYear, SanitizeForLog(memberId), lineOfBusiness, status);

        var scores = await _riskScoreRepository.SearchAsync(
            measurementYear, memberId, lineOfBusiness, status, minScore, maxScore, page, pageSize);

        return Ok(scores);
    }

    /// <summary>
    /// Batch update score status for multiple members (e.g., mark as Submitted).
    /// </summary>
    [HttpPost("scores/batch-status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> BatchUpdateStatus([FromBody] BatchStatusUpdate request)
    {
        _logger.LogInformation(
            "Batch status update: {Count} members, year={Year}, status={Status}",
            request.MemberIds.Count, request.MeasurementYear, request.Status);

        if (request.MemberIds.Count == 0)
            return BadRequest("At least one member ID is required");

        int updated = 0;
        int notFound = 0;

        foreach (var memberId in request.MemberIds)
        {
            var score = await _riskScoreRepository.GetByMemberAndYearAsync(
                memberId, request.MeasurementYear);

            if (score == null)
            {
                notFound++;
                continue;
            }

            score.Status = request.Status;
            score.LastUpdatedDate = DateTime.UtcNow;

            if (request.Status == ScoreStatus.Submitted)
            {
                score.IsSubmitted = true;
                score.SubmittedDate = DateTime.UtcNow;
            }

            await _riskScoreRepository.UpdateAsync(score);
            updated++;
        }

        return Ok(new
        {
            requested = request.MemberIds.Count,
            updated,
            notFound,
            status = request.Status.ToString()
        });
    }

    /// <summary>
    /// Get risk score by document ID.
    /// </summary>
    [HttpGet("scores/{id}")]
    [ProducesResponseType(typeof(MemberRiskScore), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MemberRiskScore>> GetScoreById(string id)
    {
        var score = await _riskScoreRepository.GetByIdAsync(id);
        if (score == null)
            return NotFound($"Risk score {id} not found");

        return Ok(score);
    }

    /// <summary>
    /// Delete a risk score record.
    /// </summary>
    [HttpDelete("scores/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteScore(string id)
    {
        _logger.LogInformation("Deleting risk score: {Id}", SanitizeForLog(id));

        var score = await _riskScoreRepository.GetByIdAsync(id);
        if (score == null)
            return NotFound($"Risk score {id} not found");

        await _riskScoreRepository.DeleteAsync(id);
        return NoContent();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
