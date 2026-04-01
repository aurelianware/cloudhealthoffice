using Microsoft.AspNetCore.Mvc;
using RfaiService.Models;
using RfaiService.Repositories;
using RfaiService.Services;

namespace RfaiService.Controllers;

[ApiController]
[Route("api/rfai")]
[Produces("application/json")]
public class RfaiController : ControllerBase
{
    private readonly IRfaiRepository _repository;
    private readonly ILogger<RfaiController> _logger;
    private readonly IKafkaProducerService? _kafkaProducer;
    private readonly IConfiguration _configuration;

    public RfaiController(
        IRfaiRepository repository,
        ILogger<RfaiController> logger,
        IConfiguration configuration,
        IKafkaProducerService? kafkaProducer = null)
    {
        _repository = repository;
        _logger = logger;
        _configuration = configuration;
        _kafkaProducer = kafkaProducer;
    }

    private string TenantId =>
        HttpContext.Items["TenantId"]?.ToString() ?? "default-tenant";

    /// <summary>
    /// Create a new RFAI case.
    /// authNumber maps to TRN02 of the originating 278 transaction.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RfaiCase>> CreateCase([FromBody] CreateRfaiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AuthNumber))
            return BadRequest("authNumber is required.");

        if (!System.Text.RegularExpressions.Regex.IsMatch(request.AuthNumber, @"^[A-Za-z0-9\-]+$"))
            return BadRequest("authNumber must be alphanumeric (hyphens allowed).");

        if (request.RequestedItems.Any(i => string.IsNullOrWhiteSpace(i.Description)))
            return BadRequest("Each requestedItem must have a non-empty description.");

        var rfaiCase = new RfaiCase
        {
            Id             = Guid.NewGuid().ToString(),
            TenantId       = TenantId,
            AuthNumber     = request.AuthNumber,
            Status         = RfaiStatus.Open,
            RequestedItems = request.RequestedItems,
            DueDate        = request.DueDate,
            Notes          = request.Notes,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };

        var created = await _repository.CreateAsync(rfaiCase);

        _logger.LogInformation(
            "RFAI case {Id} created for auth {AuthNumber} (tenant {TenantId})",
            SanitizeForLog(created.Id), SanitizeForLog(created.AuthNumber), SanitizeForLog(created.TenantId));

        return CreatedAtAction(nameof(GetCase), new { id = created.Id }, created);
    }

    /// <summary>
    /// Get an RFAI case by ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RfaiCase>> GetCase(string id)
    {
        var rfaiCase = await _repository.GetByIdAsync(TenantId, id);

        if (rfaiCase == null)
            return NotFound($"RFAI case {id} not found.");

        return Ok(rfaiCase);
    }

    /// <summary>
    /// Get all RFAI cases for a given authorization number, newest first.
    /// </summary>
    [HttpGet("by-auth/{tenantId}/{authNumber}")]
    [ProducesResponseType(typeof(IEnumerable<RfaiCase>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RfaiCase>>> GetByAuth(string tenantId, string authNumber)
    {
        var cases = await _repository.GetByAuthNumberAsync(tenantId, authNumber);
        return Ok(cases);
    }

    /// <summary>
    /// Mark an attachment as received for this RFAI case.
    /// Transitions status Open → DocsReceived.
    /// Called by attachment-service once a 275 attachment is correlated.
    /// </summary>
    [HttpPost("{id}/attachments/received")]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RfaiCase>> AttachmentReceived(
        string id,
        [FromBody] AttachmentReceivedRequest request)
    {
        var rfaiCase = await _repository.GetByIdAsync(TenantId, id);

        if (rfaiCase == null)
            return NotFound($"RFAI case {id} not found.");

        var attachment = new ReceivedAttachment
        {
            ReceivedAt               = request.ReceivedAt ?? DateTime.UtcNow,
            AttachmentControlNumber  = request.AttachmentControlNumber,
            StorageProvider          = request.StorageProvider,
            StorageKey               = request.StorageKey,
            FileHash                 = request.FileHash,
            SourceTransaction        = request.SourceTransaction,
        };

        rfaiCase.ReceivedAttachments.Add(attachment);

        var previousStatus = rfaiCase.Status;

        if (rfaiCase.Status == RfaiStatus.Open)
            rfaiCase.Status = RfaiStatus.DocsReceived;

        var updated = await _repository.UpdateAsync(rfaiCase);

        _logger.LogInformation(
            "Attachment received for RFAI case {Id} (auth {AuthNumber}), new status={Status}",
            SanitizeForLog(id), SanitizeForLog(rfaiCase.AuthNumber), rfaiCase.Status);

        // Publish Kafka event only on actual status transition to DocsReceived
        if (rfaiCase.Status == RfaiStatus.DocsReceived && previousStatus != RfaiStatus.DocsReceived && _kafkaProducer != null)
        {
            var requiredCount = rfaiCase.RequestedItems.Count(i => i.Required);
            var receivedCount = rfaiCase.ReceivedAttachments.Count;

            var kafkaMessage = new
            {
                tenantId = rfaiCase.TenantId,
                rfaiCaseId = rfaiCase.Id,
                authNumber = rfaiCase.AuthNumber,
                receivedAt = attachment.ReceivedAt,
                attachmentIds = rfaiCase.ReceivedAttachments
                    .Select(a => a.AttachmentControlNumber ?? string.Empty)
                    .ToList(),
                allRequestedItemsReceived = receivedCount >= requiredCount
            };

            var topic = _configuration["Kafka:RfaiDocsReceivedTopic"] ?? "rfai-docs-received";
            await _kafkaProducer.SendAsync(
                topic,
                rfaiCase.AuthNumber,
                kafkaMessage);
        }

        return Ok(updated);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public class CreateRfaiRequest
{
    public string AuthNumber { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public List<RequestedItem> RequestedItems { get; set; } = new();
    public string? Notes { get; set; }
}

public class AttachmentReceivedRequest
{
    public DateTime? ReceivedAt { get; set; }
    public string? AttachmentControlNumber { get; set; }
    public string? StorageProvider { get; set; }
    public string? StorageKey { get; set; }
    public string? FileHash { get; set; }
    public SourceTransaction? SourceTransaction { get; set; }
}
