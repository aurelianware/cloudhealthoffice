using Microsoft.AspNetCore.Mvc;
using RfaiService.Models;
using RfaiService.Repositories;

namespace RfaiService.Controllers;

/// <summary>
/// Manages RFAI (Request for Additional Information) cases.
/// Attachment-service can POST to <c>/api/rfai/{id}/attachments/received</c>
/// when an inbound 275 is linked to a case.
/// </summary>
[ApiController]
[Route("api/rfai")]
[Produces("application/json")]
public class RfaiController : ControllerBase
{
    private readonly IRfaiRepository _repository;
    private readonly ILogger<RfaiController> _logger;

    public RfaiController(IRfaiRepository repository, ILogger<RfaiController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    // ─── POST /api/rfai ────────────────────────────────────────────────────────

    /// <summary>Create a new RFAI case.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RfaiCase>> CreateRfaiCase([FromBody] CreateRfaiCaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
            return BadRequest("tenantId is required.");

        if (string.IsNullOrWhiteSpace(request.AuthNumber))
            return BadRequest("authNumber is required.");

        if (!IsAlphanumeric(request.AuthNumber))
            return BadRequest("authNumber must be alphanumeric.");

        if (request.RequestedItems != null)
        {
            foreach (var item in request.RequestedItems)
            {
                if (string.IsNullOrWhiteSpace(item.Description))
                    return BadRequest("Each requestedItem must have a non-empty description.");
            }
        }

        var rfaiCase = new RfaiCase
        {
            TenantId = request.TenantId,
            AuthNumber = request.AuthNumber,
            DueDate = request.DueDate,
            RequestedItems = request.RequestedItems ?? new List<RequestedItem>(),
            Notes = request.Notes,
            Status = RfaiStatus.Open
        };

        var created = await _repository.CreateAsync(rfaiCase);

        _logger.LogInformation(
            "Created RFAI case {Id} for auth {AuthNumber} tenant {TenantId}",
            created.Id,
            SanitizeForLog(created.AuthNumber),
            SanitizeForLog(created.TenantId));

        return CreatedAtAction(nameof(GetRfaiCaseById), new { id = created.Id }, created);
    }

    // ─── GET /api/rfai/{id} ────────────────────────────────────────────────────

    /// <summary>Get an RFAI case by ID.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RfaiCase>> GetRfaiCaseById(string id)
    {
        _logger.LogInformation("Fetching RFAI case {Id}", SanitizeForLog(id));

        var rfaiCase = await _repository.GetByIdAsync(id);
        if (rfaiCase == null)
            return NotFound($"RFAI case {id} not found.");

        return Ok(rfaiCase);
    }

    // ─── GET /api/rfai/by-auth/{tenantId}/{authNumber} ────────────────────────

    /// <summary>Get open / most-recent RFAI cases for a given authorization number.</summary>
    [HttpGet("by-auth/{tenantId}/{authNumber}")]
    [ProducesResponseType(typeof(IEnumerable<RfaiCase>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RfaiCase>>> GetByAuthNumber(
        string tenantId,
        string authNumber)
    {
        _logger.LogInformation(
            "Fetching RFAI cases for tenant {TenantId} auth {AuthNumber}",
            SanitizeForLog(tenantId),
            SanitizeForLog(authNumber));

        var cases = await _repository.GetByAuthNumberAsync(tenantId, authNumber);
        return Ok(cases);
    }

    // ─── POST /api/rfai/{id}/attachments/received ─────────────────────────────

    /// <summary>
    /// Mark that an attachment was received for this RFAI case.
    /// Transitions status from <c>Open</c> to <c>DocsReceived</c>.
    /// Called by attachment-service when an inbound 275 is linked to this RFAI.
    /// </summary>
    [HttpPost("{id}/attachments/received")]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RfaiCase>> MarkAttachmentReceived(
        string id,
        [FromBody] AttachmentReceivedRequest request)
    {
        _logger.LogInformation("Recording received attachment for RFAI case {Id}", SanitizeForLog(id));

        var rfaiCase = await _repository.GetByIdAsync(id);
        if (rfaiCase == null)
            return NotFound($"RFAI case {id} not found.");

        var attachment = new ReceivedAttachment
        {
            ReceivedAt = request.ReceivedAt ?? DateTime.UtcNow,
            AttachmentControlNumber = request.AttachmentControlNumber,
            StorageProvider = request.StorageProvider,
            StorageKey = request.StorageKey,
            FileHash = request.FileHash,
            SourceTransaction = request.SourceTransaction
        };

        rfaiCase.ReceivedAttachments.Add(attachment);

        // Transition Open → DocsReceived
        if (rfaiCase.Status == RfaiStatus.Open)
        {
            rfaiCase.Status = RfaiStatus.DocsReceived;
        }

        var updated = await _repository.UpdateAsync(rfaiCase);

        _logger.LogInformation(
            "RFAI case {Id} updated: status={Status}, totalAttachments={Count}",
            updated.Id, updated.Status, updated.ReceivedAttachments.Count);

        return Ok(updated);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static bool IsAlphanumeric(string value)
        => !string.IsNullOrEmpty(value) && value.All(char.IsLetterOrDigit);

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

/// <summary>Request body for creating a new RFAI case.</summary>
public class CreateRfaiCaseRequest
{
    public string TenantId { get; set; } = string.Empty;
    public string AuthNumber { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public List<RequestedItem>? RequestedItems { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Request body for recording a received attachment.</summary>
public class AttachmentReceivedRequest
{
    public DateTime? ReceivedAt { get; set; }
    public string? AttachmentControlNumber { get; set; }
    public string? StorageProvider { get; set; }
    public string? StorageKey { get; set; }
    public string? FileHash { get; set; }
    public SourceTransaction? SourceTransaction { get; set; }
}
