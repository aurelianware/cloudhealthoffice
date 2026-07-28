using System.ComponentModel.DataAnnotations;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// Admin API + resolver for 834 plan-code crosswalks. enrollment-import-service
/// calls <see cref="Resolve"/> instead of writing a trading partner's raw HD04
/// code straight into Coverage.PlanId — see <see cref="Enrollment834PlanCodeMapping"/>.
/// </summary>
[ApiController]
[Route("api/v1/plan-code-mappings")]
public class PlanCodeMappingsController : ControllerBase
{
    private readonly IEnrollment834PlanCodeMappingRepository _repository;
    private readonly ILogger<PlanCodeMappingsController> _logger;

    private string? TenantId => HttpContext.GetTenantId();

    public PlanCodeMappingsController(
        IEnrollment834PlanCodeMappingRepository repository,
        ILogger<PlanCodeMappingsController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>Resolves a trading partner's 834 plan code to this platform's PlanId. 404 when unmapped.</summary>
    [HttpGet("resolve")]
    [ProducesResponseType(typeof(PlanCodeMappingResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Resolve(
        [FromQuery] string groupNumber,
        [FromQuery] string insuranceLineCode,
        [FromQuery] string externalPlanCode,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(TenantId))
        {
            return BadRequest(new { Message = "X-Tenant-ID header is required" });
        }

        if (string.IsNullOrWhiteSpace(groupNumber) || string.IsNullOrWhiteSpace(insuranceLineCode)
            || string.IsNullOrWhiteSpace(externalPlanCode))
        {
            return BadRequest(new { Message = "groupNumber, insuranceLineCode, and externalPlanCode are all required" });
        }

        var mapping = await _repository.ResolveAsync(TenantId!, groupNumber, insuranceLineCode, externalPlanCode, ct);
        if (mapping is null)
        {
            return NotFound(new
            {
                GroupNumber = groupNumber,
                InsuranceLineCode = insuranceLineCode,
                ExternalPlanCode = externalPlanCode,
                Message = "No plan-code mapping found"
            });
        }

        return Ok(PlanCodeMappingResponse.From(mapping));
    }

    /// <summary>Lists mappings for the tenant, optionally narrowed to one group number.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PlanCodeMappingResponse>), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> List([FromQuery] string? groupNumber, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(TenantId))
        {
            return BadRequest(new { Message = "X-Tenant-ID header is required" });
        }

        var mappings = await _repository.ListAsync(TenantId!, groupNumber, ct);
        return Ok(mappings.Select(PlanCodeMappingResponse.From).ToList());
    }

    /// <summary>Creates a mapping (typically during employer-group onboarding).</summary>
    [HttpPost]
    [ProducesResponseType(typeof(PlanCodeMappingResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> Create([FromBody] CreatePlanCodeMappingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(TenantId))
        {
            return BadRequest(new { Message = "X-Tenant-ID header is required" });
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var mapping = new Enrollment834PlanCodeMapping
        {
            TenantId = TenantId!,
            GroupNumber = request.GroupNumber,
            InsuranceLineCode = request.InsuranceLineCode,
            ExternalPlanCode = request.ExternalPlanCode,
            PlanId = request.PlanId,
            CreatedBy = User.Identity?.Name ?? "System"
        };

        Enrollment834PlanCodeMapping created;
        try
        {
            created = await _repository.CreateAsync(mapping, ct);
        }
        catch (DuplicatePlanCodeMappingException ex)
        {
            return Conflict(new { Message = ex.Message });
        }

        _logger.LogInformation(
            "Created 834 plan-code mapping for group {GroupNumber}: {ExternalCode} -> {PlanId}",
            SanitizeForLog(request.GroupNumber), SanitizeForLog(request.ExternalPlanCode), SanitizeForLog(request.PlanId));

        return CreatedAtAction(nameof(List), new { groupNumber = created.GroupNumber }, PlanCodeMappingResponse.From(created));
    }

    /// <summary>
    /// Creates many mappings in one call — the onboarding path for loading an
    /// employer's full plan-code crosswalk (e.g. from a trading-partner
    /// spreadsheet) instead of one row at a time. Partial success: rows that
    /// fail (bad input or an existing duplicate) are reported per-index in
    /// <see cref="BulkPlanCodeMappingResult.Errors"/> rather than aborting the
    /// whole batch, so one bad row doesn't force a full resubmission.
    /// </summary>
    [HttpPost("bulk")]
    [ProducesResponseType(typeof(BulkPlanCodeMappingResult), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> CreateBulk([FromBody] List<CreatePlanCodeMappingRequest> requests, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(TenantId))
        {
            return BadRequest(new { Message = "X-Tenant-ID header is required" });
        }

        if (requests is null || requests.Count == 0)
        {
            return BadRequest(new { Message = "At least one mapping is required" });
        }

        var result = new BulkPlanCodeMappingResult();

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            if (string.IsNullOrWhiteSpace(request.GroupNumber) || string.IsNullOrWhiteSpace(request.InsuranceLineCode)
                || string.IsNullOrWhiteSpace(request.ExternalPlanCode) || string.IsNullOrWhiteSpace(request.PlanId))
            {
                result.Errors.Add(new BulkPlanCodeMappingError
                {
                    Index = i,
                    GroupNumber = request.GroupNumber,
                    ExternalPlanCode = request.ExternalPlanCode,
                    Error = "GroupNumber, InsuranceLineCode, ExternalPlanCode, and PlanId are all required"
                });
                continue;
            }

            try
            {
                var created = await _repository.CreateAsync(new Enrollment834PlanCodeMapping
                {
                    TenantId = TenantId!,
                    GroupNumber = request.GroupNumber,
                    InsuranceLineCode = request.InsuranceLineCode,
                    ExternalPlanCode = request.ExternalPlanCode,
                    PlanId = request.PlanId,
                    CreatedBy = User.Identity?.Name ?? "System"
                }, ct);
                result.Created.Add(PlanCodeMappingResponse.From(created));
            }
            catch (DuplicatePlanCodeMappingException ex)
            {
                result.Errors.Add(new BulkPlanCodeMappingError
                {
                    Index = i,
                    GroupNumber = request.GroupNumber,
                    ExternalPlanCode = request.ExternalPlanCode,
                    Error = ex.Message
                });
            }
        }

        _logger.LogInformation(
            "Bulk plan-code mapping load: {CreatedCount} created, {ErrorCount} errors out of {TotalCount}",
            result.Created.Count, result.Errors.Count, requests.Count);

        return Ok(result);
    }

    /// <summary>Deletes a mapping.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Delete([FromRoute] string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(TenantId))
        {
            return BadRequest(new { Message = "X-Tenant-ID header is required" });
        }

        var deleted = await _repository.DeleteAsync(TenantId!, id, ct);
        if (!deleted)
        {
            return NotFound(new { Id = id, Message = "Mapping not found" });
        }

        return NoContent();
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

public class CreatePlanCodeMappingRequest
{
    [Required]
    public string GroupNumber { get; set; } = string.Empty;

    [Required]
    public string InsuranceLineCode { get; set; } = string.Empty;

    [Required]
    public string ExternalPlanCode { get; set; } = string.Empty;

    [Required]
    public string PlanId { get; set; } = string.Empty;
}

public class PlanCodeMappingResponse
{
    public string Id { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string InsuranceLineCode { get; set; } = string.Empty;
    public string ExternalPlanCode { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public static PlanCodeMappingResponse From(Enrollment834PlanCodeMapping m) => new()
    {
        Id = m.Id,
        GroupNumber = m.GroupNumber,
        InsuranceLineCode = m.InsuranceLineCode,
        ExternalPlanCode = m.ExternalPlanCode,
        PlanId = m.PlanId,
        CreatedAt = m.CreatedAt
    };
}

public class BulkPlanCodeMappingResult
{
    public List<PlanCodeMappingResponse> Created { get; set; } = new();
    public List<BulkPlanCodeMappingError> Errors { get; set; } = new();
}

public class BulkPlanCodeMappingError
{
    public int Index { get; set; }
    public string GroupNumber { get; set; } = string.Empty;
    public string ExternalPlanCode { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
