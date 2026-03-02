using Microsoft.AspNetCore.Mvc;
using AppealsService.Models;
using AppealsService.Repositories;

namespace AppealsService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class AppealsController : ControllerBase
{
    private readonly IAppealRepository _appealRepository;
    private readonly ILogger<AppealsController> _logger;

    public AppealsController(
        IAppealRepository appealRepository,
        ILogger<AppealsController> logger)
    {
        _appealRepository = appealRepository;
        _logger = logger;
    }

    /// <summary>
    /// Submit new appeal
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Appeal>> SubmitAppeal([FromBody] Appeal appeal)
    {
        _logger.LogInformation("Submitting appeal for claim {ClaimId}", SanitizeForLog(appeal.ClaimId));

        // Validation
        if (string.IsNullOrEmpty(appeal.ClaimId))
        {
            return BadRequest("Claim ID is required");
        }

        if (string.IsNullOrEmpty(appeal.AppealReason))
        {
            return BadRequest("Appeal reason is required");
        }

        // Generate appeal number
        if (string.IsNullOrEmpty(appeal.AppealNumber))
        {
            appeal.AppealNumber = $"APL-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}";
        }

        var created = await _appealRepository.CreateAsync(appeal);

        return CreatedAtAction(
            nameof(GetAppealById),
            new { id = created.Id },
            created);
    }

    /// <summary>
    /// Get appeal by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Appeal>> GetAppealById(string id)
    {
        var appeal = await _appealRepository.GetByIdAsync(id);

        if (appeal == null)
        {
            return NotFound($"Appeal {id} not found");
        }

        return Ok(appeal);
    }

    /// <summary>
    /// Get appeal by appeal number
    /// </summary>
    [HttpGet("number/{appealNumber}")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Appeal>> GetAppealByNumber(string appealNumber)
    {
        var appeal = await _appealRepository.GetByAppealNumberAsync(appealNumber);

        if (appeal == null)
        {
            return NotFound($"Appeal {appealNumber} not found");
        }

        return Ok(appeal);
    }

    /// <summary>
    /// Get appeals for a specific claim
    /// </summary>
    [HttpGet("claim/{claimId}")]
    [ProducesResponseType(typeof(IEnumerable<Appeal>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Appeal>>> GetAppealsByClaimId(string claimId)
    {
        var appeals = await _appealRepository.GetByClaimIdAsync(claimId);
        return Ok(appeals);
    }

    /// <summary>
    /// Search appeals with filters
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Appeal>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Appeal>>> SearchAppeals(
        [FromQuery] string? memberId,
        [FromQuery] string? providerNPI,
        [FromQuery] DateTime? submittedFrom,
        [FromQuery] DateTime? submittedTo,
        [FromQuery] AppealStatus? status,
        [FromQuery] LineOfBusiness? lineOfBusiness,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation("Searching appeals: member {Member}, provider {Provider}, status {Status}",
            SanitizeForLog(memberId), SanitizeForLog(providerNPI), status);

        var appeals = await _appealRepository.SearchAsync(
            memberId, providerNPI, submittedFrom, submittedTo, status, lineOfBusiness, page, pageSize);

        return Ok(appeals);
    }

    /// <summary>
    /// Add attachment to appeal (275 submission)
    /// </summary>
    [HttpPost("{id}/attachments")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Appeal>> AddAttachment(string id, [FromBody] AppealAttachment attachment)
    {
        var appeal = await _appealRepository.GetByIdAsync(id);

        if (appeal == null)
        {
            return NotFound($"Appeal {id} not found");
        }

        // Generate 275 control number
        if (string.IsNullOrEmpty(attachment.ControlNumber))
        {
            attachment.ControlNumber = $"275-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper()}";
        }

        appeal.Attachments.Add(attachment);
        appeal.AttachmentControlNumbers.Add(attachment.ControlNumber);

        var updated = await _appealRepository.UpdateAsync(appeal);

        _logger.LogInformation("Added attachment {AttachmentId} to appeal {AppealId}", 
            SanitizeForLog(attachment.AttachmentId), SanitizeForLog(id));

        return Ok(updated);
    }

    /// <summary>
    /// Update attachment status (275 acknowledgment)
    /// </summary>
    [HttpPut("{id}/attachments/{attachmentId}")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Appeal>> UpdateAttachmentStatus(
        string id, 
        string attachmentId, 
        [FromBody] UpdateAttachmentStatusRequest request)
    {
        var appeal = await _appealRepository.GetByIdAsync(id);

        if (appeal == null)
        {
            return NotFound($"Appeal {id} not found");
        }

        var attachment = appeal.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId);
        if (attachment == null)
        {
            return NotFound($"Attachment {attachmentId} not found");
        }

        attachment.Status = request.Status;
        attachment.AcknowledgmentReceived = request.Status == AttachmentStatus.Acknowledged;
        if (request.Status == AttachmentStatus.Sent)
        {
            attachment.SentDate = DateTime.UtcNow;
        }

        var updated = await _appealRepository.UpdateAsync(appeal);

        return Ok(updated);
    }

    /// <summary>
    /// Add note to appeal
    /// </summary>
    [HttpPost("{id}/notes")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Appeal>> AddNote(string id, [FromBody] AddNoteRequest request)
    {
        var appeal = await _appealRepository.GetByIdAsync(id);

        if (appeal == null)
        {
            return NotFound($"Appeal {id} not found");
        }

        appeal.Notes.Add(new AppealNote
        {
            NoteText = request.NoteText,
            CreatedBy = request.CreatedBy,
            IsInternal = request.IsInternal
        });

        var updated = await _appealRepository.UpdateAsync(appeal);

        return Ok(updated);
    }

    /// <summary>
    /// Submit appeal decision
    /// </summary>
    [HttpPost("{id}/decision")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Appeal>> SubmitDecision(string id, [FromBody] AppealDecision decision)
    {
        var appeal = await _appealRepository.GetByIdAsync(id);

        if (appeal == null)
        {
            return NotFound($"Appeal {id} not found");
        }

        appeal.Decision = decision;
        appeal.DecisionDate = decision.DecisionDate;
        appeal.Status = decision.DecisionType switch
        {
            AppealDecisionType.Approved => AppealStatus.Approved,
            AppealDecisionType.Denied => AppealStatus.Denied,
            AppealDecisionType.PartialApproval => AppealStatus.PartialApproval,
            _ => appeal.Status
        };

        var updated = await _appealRepository.UpdateAsync(appeal);

        _logger.LogInformation("Appeal {AppealId} decision: {Decision}", SanitizeForLog(id), decision.DecisionType);

        return Ok(updated);
    }

    /// <summary>
    /// Update appeal status
    /// </summary>
    [HttpPut("{id}/status")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Appeal>> UpdateStatus(string id, [FromBody] UpdateStatusRequest request)
    {
        var appeal = await _appealRepository.GetByIdAsync(id);

        if (appeal == null)
        {
            return NotFound($"Appeal {id} not found");
        }

        appeal.Status = request.Status;

        var updated = await _appealRepository.UpdateAsync(appeal);

        return Ok(updated);
    }

    /// <summary>
    /// Get appeals summary statistics
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(AppealsSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<AppealsSummary>> GetAppealsSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        var fromDate = from ?? DateTime.UtcNow.AddMonths(-3);
        var toDate = to ?? DateTime.UtcNow;

        var summary = await _appealRepository.GetAppealsSummaryAsync(fromDate, toDate);

        return Ok(summary);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class UpdateAttachmentStatusRequest
{
    public AttachmentStatus Status { get; set; }
}

public class AddNoteRequest
{
    public string NoteText { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public bool IsInternal { get; set; } = true;
}

public class UpdateStatusRequest
{
    public AppealStatus Status { get; set; }
}

