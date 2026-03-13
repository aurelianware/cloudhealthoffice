using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RiskAdjustmentService.Models;
using RiskAdjustmentService.Repositories;
using CloudHealthOffice.RiskAdjustmentEngine.Domain;
using CloudHealthOffice.RiskAdjustmentEngine.Services;
using EngineRiskAdjustmentEngine = CloudHealthOffice.RiskAdjustmentEngine.Services.RiskAdjustmentEngine;
using ServiceHccCategory = RiskAdjustmentService.Models.HccCategory;

namespace RiskAdjustmentService.Controllers;

[ApiController]
[Route("api/risk-adjustment")]
[Produces("application/json")]
public class RiskAdjustmentController : ControllerBase
{
    private readonly IRiskScoreRepository _riskScoreRepository;
    private readonly EngineRiskAdjustmentEngine _riskEngine;
    private readonly ILogger<RiskAdjustmentController> _logger;

    public RiskAdjustmentController(
        IRiskScoreRepository riskScoreRepository,
        EngineRiskAdjustmentEngine riskEngine,
        ILogger<RiskAdjustmentController> logger)
    {
        _riskScoreRepository = riskScoreRepository;
        _riskEngine = riskEngine;
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

        if ((score.HccCategories?.Count ?? 0) == 0 && score.RiskScore == 0 && score.DemographicFactor == 0)
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
    /// Calculate a risk score for a member using the HCC scoring engine.
    /// Provide AgeAsOfPaymentYear, Gender, and DiagnosisCodes in the request body
    /// to invoke full CMS-HCC v28 / HHS-HCC scoring. The result is persisted and returned.
    /// </summary>
    [HttpPost("scores/calculate")]
    [ProducesResponseType(typeof(MemberRiskScore), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MemberRiskScore>> RequestScoreCalculation(
        [FromBody] ScoreCalculationRequest request)
    {
        _logger.LogInformation(
            "Score calculation requested for member {MemberId}, year {Year}, model {Model}",
            SanitizeForLog(request.MemberId), request.MeasurementYear, SanitizeForLog(request.RiskModel));

        var hccModel = MapHccModel(request.RiskModel);
        var gender   = MapGender(request.Gender);
        var age      = request.AgeAsOfPaymentYear ?? 0;

        var engineInput = new RiskScoreInput
        {
            MemberId           = request.MemberId,
            SubscriberId       = request.SubscriberId ?? request.MemberId,
            Model              = hccModel,
            Segment            = EnrollmentSegment.CommunityNonDual,
            AgeAsOfPaymentYear = age,
            Gender             = gender,
            DiagnosisCodes     = [.. request.DiagnosisCodes]
        };

        var engineResult = _riskEngine.ComputeRiskScore(engineInput);

        var suppressedSet = engineResult.SuppressedHccs.ToHashSet();

        var hccCategories = engineResult.HccContributions
            .Select(c => new ServiceHccCategory
            {
                CategoryCode         = c.CategoryCode.ToString(),
                Coefficient          = c.RelativeFactor,
                SourceDiagnosisCodes = c.SourceDiagnosisCodes,
                IsSuperseded         = false
            })
            .ToList();

        // Include suppressed HCCs (zero coefficient, marked as superseded)
        foreach (var suppressed in engineResult.SuppressedHccs)
        {
            var sourceDx = engineResult.DiagnosisToHccMap
                .Where(kvp => kvp.Value == suppressed)
                .Select(kvp => kvp.Key)
                .ToList();

            hccCategories.Add(new ServiceHccCategory
            {
                CategoryCode         = suppressed.ToString(),
                Coefficient          = 0m,
                SourceDiagnosisCodes = sourceDx,
                IsSuperseded         = true
            });
        }

        var diagnoses = engineResult.DiagnosisToHccMap
            .Select(kvp => new RiskDiagnosis
            {
                DiagnosisCode    = kvp.Key,
                MappedHccCategory = kvp.Value?.ToString()
            })
            .ToList();

        var existing = await _riskScoreRepository.GetByMemberAndYearAsync(
            request.MemberId, request.MeasurementYear);

        if (existing != null)
        {
            existing.RiskModel        = request.RiskModel;
            existing.ModelVersion     = request.ModelVersion;
            existing.LineOfBusiness   = request.LineOfBusiness;
            existing.MemberFirstName  = request.MemberFirstName ?? existing.MemberFirstName;
            existing.MemberLastName   = request.MemberLastName  ?? existing.MemberLastName;
            existing.Gender           = request.Gender          ?? existing.Gender;
            existing.DemographicFactor = engineResult.DemographicFactor;
            existing.HccFactor        = engineResult.TotalHccFactor;
            existing.RiskScore        = engineResult.FinalRiskScore;
            existing.HccCategories    = hccCategories;
            existing.Diagnoses        = diagnoses;
            existing.Status           = ScoreStatus.Calculated;
            existing.CalculatedDate   = DateTime.UtcNow;
            existing.LastUpdatedDate  = DateTime.UtcNow;

            var updated = await _riskScoreRepository.UpdateAsync(existing);
            return Ok(updated);
        }

        var score = new MemberRiskScore
        {
            Id                = Guid.NewGuid().ToString(),
            MemberId          = request.MemberId,
            MemberFirstName   = request.MemberFirstName,
            MemberLastName    = request.MemberLastName,
            Gender            = request.Gender,
            MeasurementYear   = request.MeasurementYear,
            RiskModel         = request.RiskModel,
            ModelVersion      = request.ModelVersion,
            LineOfBusiness    = request.LineOfBusiness,
            DemographicFactor = engineResult.DemographicFactor,
            HccFactor         = engineResult.TotalHccFactor,
            RiskScore         = engineResult.FinalRiskScore,
            HccCategories     = hccCategories,
            Diagnoses         = diagnoses,
            Status            = ScoreStatus.Calculated,
            CalculatedDate    = DateTime.UtcNow,
            CreatedDate       = DateTime.UtcNow,
            LastUpdatedDate   = DateTime.UtcNow
        };

        var created = await _riskScoreRepository.CreateAsync(score);
        return Ok(created);
    }

    private static HccModel MapHccModel(string riskModel) => riskModel?.ToUpperInvariant() switch
    {
        "HHS-HCC" or "HHS_HCC" or "HHSHCC" => HccModel.HhsHcc,
        _ => HccModel.CmsHccV28
    };

    private static MemberGender MapGender(string? gender) => gender?.ToUpperInvariant() switch
    {
        "M" or "MALE"   => MemberGender.Male,
        "F" or "FEMALE" => MemberGender.Female,
        _ => MemberGender.Female   // default
    };

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
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 200)] int pageSize = 50)
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
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 200)] int pageSize = 50)
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
