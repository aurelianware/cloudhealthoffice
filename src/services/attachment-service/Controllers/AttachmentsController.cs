using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using AttachmentService.Models;
using AttachmentService.Repositories;
using AttachmentService.Services;
using CloudHealthOffice.DocumentStore;
using System.Security.Cryptography;

namespace AttachmentService.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class AttachmentsController : ControllerBase
{
    private readonly IAttachmentRepository _repository;
    private readonly IAcknowledgmentService _acknowledgmentService;
    private readonly IDocumentStore _documentStore;
    private readonly ILogger<AttachmentsController> _logger;

    public AttachmentsController(
        IAttachmentRepository repository,
        IAcknowledgmentService acknowledgmentService,
        IDocumentStore documentStore,
        ILogger<AttachmentsController> logger)
    {
        _repository = repository;
        _acknowledgmentService = acknowledgmentService;
        _documentStore = documentStore;
        _logger = logger;
    }

    /// <summary>
    /// Submit a 275 attachment with optional file upload
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Attachment>> CreateAttachment(
        [FromForm] AttachmentRequest request,
        [FromForm] IFormFile? file)
    {
        try
        {
            var attachment = new Attachment
            {
                TenantId = request.TenantId,
                ClaimId = request.ClaimId,
                AuthorizationId = request.AuthorizationId,
                AppealId = request.AppealId,
                RFAIReference = request.RFAIReference,
                AttachmentType = string.IsNullOrWhiteSpace(request.RFAIReference) ? "Unsolicited" : "Solicited",
                PayerId = request.PayerId,
                PayerName = request.PayerName,
                ProviderId = request.ProviderId,
                ProviderName = request.ProviderName,
                SubscriberId = request.SubscriberId,
                PatientFirstName = request.PatientFirstName,
                PatientLastName = request.PatientLastName,
                DocumentType = request.DocumentType,
                DocumentFormat = request.DocumentFormat,
                RawX12 = request.RawX12,
                SubmittedDate = request.SubmittedDate ?? DateTime.UtcNow,
                Status = "Received"
            };

            // Validate exactly one parent entity is specified
            var parentCount = new[] { attachment.ClaimId, attachment.AuthorizationId, attachment.AppealId }
                .Count(x => !string.IsNullOrWhiteSpace(x));

            if (parentCount == 0)
            {
                return BadRequest("Must specify one of: ClaimId, AuthorizationId, or AppealId");
            }

            if (parentCount > 1)
            {
                return BadRequest("Cannot specify multiple parent entities (ClaimId, AuthorizationId, AppealId)");
            }

            // Upload file to document store if provided
            if (file != null && file.Length > 0)
            {
                const string containerName = "attachments";

                // Generate blob name: tenantId/parentType/parentId/attachmentId.ext
                var parentType = !string.IsNullOrWhiteSpace(attachment.ClaimId) ? "claims" :
                                 !string.IsNullOrWhiteSpace(attachment.AuthorizationId) ? "authorizations" : "appeals";
                var parentId   = attachment.ClaimId ?? attachment.AuthorizationId ?? attachment.AppealId;
                var extension  = Path.GetExtension(file.FileName).ToLowerInvariant();
                var blobName   = $"{attachment.TenantId}/{parentType}/{parentId}/{attachment.Id}{extension}";

                // Compute SHA-256 hash before upload
                using var stream = file.OpenReadStream();
                using var sha256 = SHA256.Create();
                var hashBytes = await sha256.ComputeHashAsync(stream);
                var hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                stream.Position = 0;
                var contentType = $"application/{attachment.DocumentFormat.ToLowerInvariant()}";
                var uploaded = await _documentStore.UploadAsync(containerName, blobName, stream, contentType);

                attachment.BlobUrl           = uploaded.Uri.ToString();
                attachment.BlobContainerName = containerName;
                attachment.BlobName          = blobName;
                attachment.FileSizeBytes      = file.Length;
                attachment.FileHash          = hash;
                attachment.Status            = "Validated";
            }

            // If this is a solicited attachment (RFAI response), link it to the authorization
            if (!string.IsNullOrWhiteSpace(attachment.RFAIReference) && 
                !string.IsNullOrWhiteSpace(attachment.AuthorizationId))
            {
                attachment.Status = "Linked";
                attachment.Notes = $"Linked to Authorization {attachment.AuthorizationId} via RFAI {attachment.RFAIReference}";
            }

            var created = await _repository.CreateAsync(attachment);
            _logger.LogInformation(
                "Created attachment {AttachmentId} for {ParentType} {ParentId}",
                created.Id,
                !string.IsNullOrWhiteSpace(created.ClaimId) ? "Claim" :
                !string.IsNullOrWhiteSpace(created.AuthorizationId) ? "Authorization" : "Appeal",
                SanitizeForLog(created.ClaimId ?? created.AuthorizationId ?? created.AppealId));

            return CreatedAtAction(nameof(GetAttachment), new { id = created.Id, tenantId = created.TenantId }, created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating attachment");
            return StatusCode(500, new { error = "Failed to create attachment", details = ex.Message });
        }
    }

    /// <summary>
    /// Get attachment by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Attachment>> GetAttachment(string id, [FromQuery] string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest("tenantId query parameter is required");
        }

        var attachment = await _repository.GetByIdAsync(id, tenantId);
        if (attachment == null)
        {
            return NotFound();
        }

        return Ok(attachment);
    }

    /// <summary>
    /// Get all attachments for a claim
    /// </summary>
    [HttpGet("claim/{claimId}")]
    public async Task<ActionResult<IEnumerable<Attachment>>> GetByClaimId(string claimId, [FromQuery] string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest("tenantId query parameter is required");
        }

        var attachments = await _repository.GetByClaimIdAsync(claimId, tenantId);
        return Ok(attachments);
    }

    /// <summary>
    /// Get all attachments for an authorization
    /// </summary>
    [HttpGet("authorization/{authorizationId}")]
    public async Task<ActionResult<IEnumerable<Attachment>>> GetByAuthorizationId(string authorizationId, [FromQuery] string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest("tenantId query parameter is required");
        }

        var attachments = await _repository.GetByAuthorizationIdAsync(authorizationId, tenantId);
        return Ok(attachments);
    }

    /// <summary>
    /// Get all attachments for an appeal
    /// </summary>
    [HttpGet("appeal/{appealId}")]
    public async Task<ActionResult<IEnumerable<Attachment>>> GetByAppealId(string appealId, [FromQuery] string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest("tenantId query parameter is required");
        }

        var attachments = await _repository.GetByAppealIdAsync(appealId, tenantId);
        return Ok(attachments);
    }

    /// <summary>
    /// Get attachment by RFAI reference (solicited attachments)
    /// </summary>
    [HttpGet("rfai/{rfaiReference}")]
    public async Task<ActionResult<Attachment>> GetByRFAIReference(string rfaiReference, [FromQuery] string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest("tenantId query parameter is required");
        }

        var attachment = await _repository.GetByRFAIReferenceAsync(rfaiReference, tenantId);
        if (attachment == null)
        {
            return NotFound();
        }

        return Ok(attachment);
    }

    /// <summary>
    /// Download attachment file from Blob Storage
    /// </summary>
    [HttpGet("{id}/download")]
    public async Task<IActionResult> DownloadAttachment(string id, [FromQuery] string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest("tenantId query parameter is required");
        }

        var attachment = await _repository.GetByIdAsync(id, tenantId);
        if (attachment == null || string.IsNullOrWhiteSpace(attachment.BlobName))
        {
            return NotFound();
        }

        if (!await _documentStore.ExistsAsync(attachment.BlobContainerName!, attachment.BlobName))
        {
            return NotFound("File not found in storage");
        }

        var stream      = await _documentStore.DownloadAsync(attachment.BlobContainerName!, attachment.BlobName);
        var contentType = $"application/{attachment.DocumentFormat.ToLowerInvariant()}";
        var fileName    = $"{attachment.Id}.{attachment.DocumentFormat.ToLowerInvariant()}";

        return File(stream, contentType, fileName);
    }

    /// <summary>
    /// Generate acknowledgment (999 or 824) for an attachment based on trading partner config
    /// </summary>
    [HttpPost("{id}/acknowledgment")]
    public async Task<ActionResult<AcknowledgmentResponse>> GenerateAcknowledgment(
        string id, 
        [FromQuery] string tenantId,
        [FromQuery] bool autoSend = false)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest("tenantId query parameter is required");
        }

        var attachment = await _repository.GetByIdAsync(id, tenantId);
        if (attachment == null)
        {
            return NotFound();
        }

        if (attachment.AcknowledgmentSent)
        {
            return BadRequest($"Acknowledgment already sent on {attachment.AcknowledgmentSentDate}");
        }

        try
        {
            // Get trading partner configuration
            var tradingPartner = await _acknowledgmentService.GetTradingPartnerByPayerIdAsync(
                attachment.PayerId, 
                tenantId);

            var ackType = _acknowledgmentService.GetAcknowledgmentType(tradingPartner);

            // Generate appropriate acknowledgment(s)
            if (ackType == "999" || ackType == "Both")
            {
                attachment.Generated999 = await _acknowledgmentService.Generate999Async(attachment, tradingPartner ?? CreateDefaultTradingPartner(attachment));
            }

            if (ackType == "824" || ackType == "Both")
            {
                attachment.Generated824 = await _acknowledgmentService.Generate824Async(attachment, tradingPartner ?? CreateDefaultTradingPartner(attachment));
            }

            attachment.AcknowledgmentType = ackType;
            
            if (autoSend || tradingPartner?.AutoSendAcknowledgments == true)
            {
                attachment.AcknowledgmentSent = true;
                attachment.AcknowledgmentSentDate = DateTime.UtcNow;
            }

            var updated = await _repository.UpdateAsync(attachment);

            _logger.LogInformation(
                "Generated {AckType} acknowledgment for attachment {AttachmentId}",
                ackType,
                SanitizeForLog(id));

            return Ok(new AcknowledgmentResponse
            {
                AttachmentId = updated.Id,
                AcknowledgmentType = ackType,
                Generated999 = updated.Generated999,
                Generated824 = updated.Generated824,
                AcknowledgmentSent = updated.AcknowledgmentSent,
                AcknowledgmentSentDate = updated.AcknowledgmentSentDate,
                TradingPartnerFound = tradingPartner != null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating acknowledgment for attachment {AttachmentId}", SanitizeForLog(id));
            return StatusCode(500, new { error = "Failed to generate acknowledgment", details = ex.Message });
        }
    }

    private TradingPartner CreateDefaultTradingPartner(Attachment attachment)
    {
        return new TradingPartner
        {
            TenantId = attachment.TenantId,
            PartnerId = attachment.PayerId,
            PartnerName = attachment.PayerName,
            AttachmentAckType = "999", // Default to 999
            InterchangeSenderId = "SENDER",
            InterchangeReceiverId = attachment.ProviderId,
            ApplicationSenderId = "SENDER",
            ApplicationReceiverId = attachment.ProviderId
        };
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// Response model for acknowledgment generation
/// </summary>
public class AcknowledgmentResponse
{
    public string AttachmentId { get; set; } = string.Empty;
    public string AcknowledgmentType { get; set; } = string.Empty;
    public string? Generated999 { get; set; }
    public string? Generated824 { get; set; }
    public bool AcknowledgmentSent { get; set; }
    public DateTime? AcknowledgmentSentDate { get; set; }
    public bool TradingPartnerFound { get; set; }
}

/// <summary>
/// Request model for creating attachments
/// </summary>
public class AttachmentRequest
{
    public string TenantId { get; set; } = string.Empty;
    public string? ClaimId { get; set; }
    public string? AuthorizationId { get; set; }
    public string? AppealId { get; set; }
    public string? RFAIReference { get; set; }
    public string PayerId { get; set; } = string.Empty;
    public string PayerName { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string? ProviderName { get; set; }
    public string SubscriberId { get; set; } = string.Empty;
    public string? PatientFirstName { get; set; }
    public string? PatientLastName { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentFormat { get; set; } = string.Empty;
    public string? RawX12 { get; set; }
    public DateTime? SubmittedDate { get; set; }
}
