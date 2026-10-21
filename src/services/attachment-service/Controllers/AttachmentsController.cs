using Microsoft.AspNetCore.Mvc;
using AttachmentService.Models;
using AttachmentService.Repositories;
using AttachmentService.Services;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Logging;

namespace AttachmentService.Controllers;

// Access: default CHO permissions (attachments:read for GET, attachments:write
// otherwise), applied by AddChoAuthentication. The tenant is the token's tenant.
[ApiController]
[Route("api/[controller]")]
public class AttachmentsController : ControllerBase
{
    private readonly IAttachmentRepository _repository;
    private readonly IAcknowledgmentService _acknowledgmentService;
    private readonly IDocumentCatalog _catalog;
    private readonly ICurrentActor _actor;
    private readonly IAttachmentParentLookup _parents;
    private readonly IClaimAttachmentMatcher _matcher;

    private const string ClaimsRead = "claims:read";
    private readonly AttachmentUploadOptions _uploadOptions;
    private readonly ILogger<AttachmentsController> _logger;

    public AttachmentsController(
        IAttachmentRepository repository,
        IAcknowledgmentService acknowledgmentService,
        IDocumentCatalog catalog,
        ICurrentActor actor,
        IAttachmentParentLookup parents,
        IClaimAttachmentMatcher matcher,
        IOptions<AttachmentUploadOptions> uploadOptions,
        ILogger<AttachmentsController> logger)
    {
        _repository = repository;
        _acknowledgmentService = acknowledgmentService;
        _catalog = catalog;
        _actor = actor;
        _parents = parents;
        _matcher = matcher;
        _uploadOptions = uploadOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Submit a 275 attachment with optional file upload
    /// </summary>
    [HttpPost]
    [AttachmentUploadLimit]
    public async Task<ActionResult<Attachment>> CreateAttachment(
        [FromForm] AttachmentRequest request,
        [FromForm] IFormFile? file)
    {
        Stream? fileStream = null;
        try
        {
            // Tenant and submitter come from the validated token, never from the form.
            var attachment = new Attachment
            {
                TenantId = _actor.TenantId,
                CreatedBy = _actor.UserId,
                ClaimId = request.ClaimId,
                AuthorizationId = request.AuthorizationId,
                AppealId = request.AppealId,
                RFAIReference = request.RFAIReference,
                AttachmentControlNumber = string.IsNullOrWhiteSpace(request.AttachmentControlNumber) ? null : request.AttachmentControlNumber.Trim(),
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

            // Exactly one parent entity, and its id must be a plain id: it becomes a storage path segment.
            var parents = new (AttachmentParentType Type, string? Id)[]
                {
                    (AttachmentParentType.Claim, attachment.ClaimId),
                    (AttachmentParentType.Authorization, attachment.AuthorizationId),
                    (AttachmentParentType.Appeal, attachment.AppealId),
                }
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .ToList();

            if (parents.Count > 1)
            {
                return BadRequest("Cannot specify multiple parent entities (ClaimId, AuthorizationId, AppealId)");
            }

            if (attachment.AttachmentControlNumber is { } acn && !AttachmentUploadRules.IsValidControlNumber(acn))
            {
                return BadRequest("AttachmentControlNumber (TRN02) must be 1-80 letters, digits, '.', ':', '-' or '_'");
            }

            AttachmentParentType parentType;
            string parentId;
            // Set when the parent came from claims-service (matched) or from the
            // 275 itself (held): either way there is nothing more to look up.
            var parentConfirmed = false;
            if (parents.Count == 1)
            {
                (parentType, parentId) = (parents[0].Type, parents[0].Id!);
            }
            else if (attachment.AttachmentControlNumber is { } controlNumber)
            {
                // The 275 names no record: find the claim whose 837 PWK06 is its TRN02.
                // The lookup runs with this service's token and the answer names a
                // claim, so it is done inline only for a caller who may read claims;
                // anyone else's 275 is held and the background matcher links it, so
                // probing control numbers reveals no claim ids.
                IReadOnlyList<ClaimControlNumberMatch> matches = [];
                try
                {
                    if (_actor.HasPermission(ClaimsRead))
                        matches = await _matcher.FindAsync(attachment.TenantId, controlNumber, HttpContext.RequestAborted);
                }
                catch (AttachmentParentLookupException ex)
                {
                    _logger.LogError(ex, "Could not match an attachment control number (trace id {TraceId})",
                        ChoProblemResults.TraceId(HttpContext));
                    return this.ServerError("Could not look for the claim this attachment belongs to; nothing was stored. Retry later.",
                        StatusCodes.Status502BadGateway);
                }

                parentConfirmed = true;
                if (IClaimAttachmentMatcher.Choose(matches, attachment.ProviderId) is { } claimId)
                {
                    attachment.ClaimId = claimId;
                    attachment.Status = "Linked";
                    attachment.Notes = $"Matched to claim {claimId} by attachment control number {controlNumber}";
                    (parentType, parentId) = (AttachmentParentType.Claim, claimId);
                }
                else
                {
                    // Held under its provider until a claim announcing this
                    // number arrives (UnmatchedAttachmentMatcher links it then).
                    // The note says nothing about other providers' claims.
                    attachment.Status = "Unmatched";
                    attachment.Notes = $"Held until a claim from provider {attachment.ProviderId.Trim()} announces attachment control number {controlNumber}";
                    (parentType, parentId) = (AttachmentParentType.Provider, attachment.ProviderId.Trim());
                    attachment.ProviderId = parentId;
                }
            }
            else
            {
                return BadRequest("Must specify one of: ClaimId, AuthorizationId, AppealId, or AttachmentControlNumber");
            }

            if (!AttachmentUploadRules.IsValidParentId(parentId))
            {
                return BadRequest(parentType == AttachmentParentType.Provider
                    ? "ProviderId must be 1-128 letters, digits, '-' or '_' to hold an unmatched attachment"
                    : $"{parentType}Id must be 1-128 letters, digits, '-' or '_'");
            }

            // Check the file before calling another service about it.
            AttachmentFormat? format = null;
            if (file != null && file.Length > 0)
            {
                if (file.Length > _uploadOptions.MaxFileSizeBytes)
                {
                    return Problem(
                        statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "Attachment too large",
                        detail: $"Attachments are limited to {_uploadOptions.MaxFileSizeBytes} bytes.");
                }

                format = AttachmentUploadRules.ResolveFormat(attachment.DocumentFormat);
                if (format is null || !AttachmentUploadRules.ExtensionMatches(file.FileName, format))
                {
                    return Problem(
                        statusCode: StatusCodes.Status415UnsupportedMediaType,
                        title: "Unsupported attachment format",
                        detail: "DocumentFormat must be PDF, TIFF, JPEG or PNG, and the file name must have a matching extension.");
                }
            }

            // The parent must exist for the caller's tenant. The owning service answers
            // 404 for another tenant's record, so this also refuses cross-tenant ids.
            bool parentExists = parentConfirmed;
            try
            {
                if (!parentConfirmed)
                    parentExists = await _parents.ExistsAsync(attachment.TenantId, parentType, parentId, HttpContext.RequestAborted);
            }
            catch (AttachmentParentLookupException ex)
            {
                _logger.LogError(ex, "Could not confirm the {ParentType} for a new attachment (trace id {TraceId})",
                    parentType, ChoProblemResults.TraceId(HttpContext));
                return this.ServerError($"Could not confirm the {parentType.ToString().ToLowerInvariant()} exists; nothing was stored. Retry later.",
                    StatusCodes.Status502BadGateway);
            }

            if (!parentExists)
            {
                return Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: $"{parentType} not found",
                    detail: $"No {parentType.ToString().ToLowerInvariant()} {parentId} exists for this tenant.");
            }

            // The file goes to the document catalog, linked to the attachment's parent.
            // The catalog trusts this service's token, so the caller's own right to
            // file documents on that parent is checked here first.
            if (file != null && file.Length > 0 && format is not null)
            {
                if (!_actor.HasPermission(ChoRolePermissions.DocumentsWrite)
                    || !_actor.HasPermission(HttpDocumentCatalog.SubjectReadPermission(parentType)))
                {
                    return Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Not allowed to file this attachment",
                        detail: $"Filing an attachment on a {parentType.ToString().ToLowerInvariant()} needs {ChoRolePermissions.DocumentsWrite} and {HttpDocumentCatalog.SubjectReadPermission(parentType)}.");
                }

                fileStream = file.OpenReadStream();
                if (!await AttachmentUploadRules.HasSignatureAsync(fileStream, format, HttpContext.RequestAborted))
                {
                    await fileStream.DisposeAsync();
                    return Problem(
                        statusCode: StatusCodes.Status415UnsupportedMediaType,
                        title: "Unsupported attachment format",
                        detail: $"The file content is not a {format.Format} file.");
                }

                fileStream.Position = 0;
                attachment.FileHash = Convert.ToHexString(await SHA256.HashDataAsync(fileStream, HttpContext.RequestAborted)).ToLowerInvariant();
                fileStream.Position = 0;
                attachment.DocumentFormat = format.Format;
                attachment.FileSizeBytes  = file.Length;
            }

            // If this is a solicited attachment (RFAI response), link it to the authorization
            if (!string.IsNullOrWhiteSpace(attachment.RFAIReference) && 
                !string.IsNullOrWhiteSpace(attachment.AuthorizationId))
            {
                attachment.Status = "Linked";
                attachment.Notes = $"Linked to Authorization {attachment.AuthorizationId} via RFAI {attachment.RFAIReference}";
            }

            // The record is saved first, so a file in the catalog always has an
            // attachment pointing at it; if filing then fails, the record is removed.
            var created = await _repository.CreateAsync(attachment);
            if (fileStream is not null && format is not null)
            {
                var externalIds = new Dictionary<string, string> { ["AttachmentId"] = created.Id };
                if (AttachmentUploadRules.IsValidParentId(created.RFAIReference ?? string.Empty))
                    externalIds["RfaiReference"] = created.RFAIReference!;
                if (created.AttachmentControlNumber is { } acnId && AttachmentUploadRules.IsValidControlNumber(acnId))
                    externalIds["AttachmentControlNumber"] = acnId;

                try
                {
                    var stored = await _catalog.UploadAsync(new CatalogUpload(
                        created.TenantId,
                        _actor.UserId,
                        parentType,
                        parentId,
                        Title: string.IsNullOrWhiteSpace(created.DocumentType) ? "Attachment" : created.DocumentType,
                        fileStream,
                        format.ContentType,
                        ClaimAttachmentRules.SanitizePathSegment(created.Id) + format.Extension,
                        Channel: string.IsNullOrWhiteSpace(created.RawX12) ? "Upload" : "X12-275",
                        externalIds), HttpContext.RequestAborted);

                    created.DocumentId = stored.DocumentId;
                    if (created.Status == "Received")
                        created.Status = "Validated";
                    created = await _repository.UpdateAsync(created);
                }
                catch (DocumentCatalogException ex)
                {
                    await _repository.DeleteAsync(created.Id, created.TenantId);
                    _logger.LogError(ex, "Could not file an attachment in the document catalog (trace id {TraceId})",
                        ChoProblemResults.TraceId(HttpContext));
                    return CatalogFailure(ex, "store the attachment file; nothing was saved");
                }
            }
            _logger.LogInformation(
                "Created attachment {AttachmentId} for {ParentType} {ParentId}",
                created.Id,
                !string.IsNullOrWhiteSpace(created.ClaimId) ? "Claim" :
                !string.IsNullOrWhiteSpace(created.AuthorizationId) ? "Authorization" : "Appeal",
                LogSanitizer.Sanitize(created.ClaimId ?? created.AuthorizationId ?? created.AppealId));

            return CreatedAtAction(nameof(GetAttachment), new { id = created.Id }, created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating attachment (trace id {TraceId})", ChoProblemResults.TraceId(HttpContext));
            return this.ServerError("Failed to create attachment");
        }
        finally
        {
            if (fileStream is not null)
                await fileStream.DisposeAsync();
        }
    }

    /// <summary>
    /// Get attachment by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Attachment>> GetAttachment(string id)
    {
        var tenantId = _actor.TenantId;

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
    public async Task<ActionResult<IEnumerable<Attachment>>> GetByClaimId(string claimId)
    {
        var tenantId = _actor.TenantId;

        var attachments = await _repository.GetByClaimIdAsync(claimId, tenantId);
        return Ok(attachments);
    }

    /// <summary>
    /// Get all attachments for an authorization
    /// </summary>
    [HttpGet("authorization/{authorizationId}")]
    public async Task<ActionResult<IEnumerable<Attachment>>> GetByAuthorizationId(string authorizationId)
    {
        var tenantId = _actor.TenantId;

        var attachments = await _repository.GetByAuthorizationIdAsync(authorizationId, tenantId);
        return Ok(attachments);
    }

    /// <summary>
    /// Get all attachments for an appeal
    /// </summary>
    [HttpGet("appeal/{appealId}")]
    public async Task<ActionResult<IEnumerable<Attachment>>> GetByAppealId(string appealId)
    {
        var tenantId = _actor.TenantId;

        var attachments = await _repository.GetByAppealIdAsync(appealId, tenantId);
        return Ok(attachments);
    }

    /// <summary>
    /// 275s held because no claim has announced their attachment control
    /// number (TRN02) yet, oldest first. They are linked automatically when the
    /// claim arrives, and rejected after the hold period.
    /// </summary>
    [HttpGet("unmatched")]
    public async Task<ActionResult<IEnumerable<Attachment>>> GetUnmatched()
    {
        return Ok(await _repository.GetUnmatchedAsync(_actor.TenantId));
    }

    /// <summary>
    /// Get attachment by RFAI reference (solicited attachments)
    /// </summary>
    [HttpGet("rfai/{rfaiReference}")]
    public async Task<ActionResult<Attachment>> GetByRFAIReference(string rfaiReference)
    {
        var tenantId = _actor.TenantId;

        var attachment = await _repository.GetByRFAIReferenceAsync(rfaiReference, tenantId);
        if (attachment == null)
        {
            return NotFound();
        }

        return Ok(attachment);
    }

    /// <summary>
    /// Download the attachment's file from the document catalog, opened from
    /// the attachment's own claim, authorization or appeal. The catalog
    /// records the download in its access audit, for the calling user.
    /// </summary>
    [HttpGet("{id}/download")]
    public async Task<IActionResult> DownloadAttachment(string id)
    {
        var tenantId = _actor.TenantId;

        var attachment = await _repository.GetByIdAsync(id, tenantId);
        if (attachment == null || string.IsNullOrWhiteSpace(attachment.DocumentId))
        {
            return NotFound();
        }

        (AttachmentParentType Type, string Id)? parent =
            !string.IsNullOrWhiteSpace(attachment.ClaimId) ? (AttachmentParentType.Claim, attachment.ClaimId!)
            : !string.IsNullOrWhiteSpace(attachment.AuthorizationId) ? (AttachmentParentType.Authorization, attachment.AuthorizationId!)
            : !string.IsNullOrWhiteSpace(attachment.AppealId) ? (AttachmentParentType.Appeal, attachment.AppealId!)
            // Held, not yet matched: filed under its provider.
            : attachment.Status == "Unmatched" && !string.IsNullOrWhiteSpace(attachment.ProviderId)
                ? (AttachmentParentType.Provider, attachment.ProviderId)
            : null;
        if (parent is not { } p)
        {
            return NotFound();
        }

        // The catalog trusts this service's token, so the caller's own right to
        // open documents on this parent is checked here, as the catalog would.
        if (!_actor.HasPermission(ChoRolePermissions.DocumentsRead)
            || !_actor.HasPermission(HttpDocumentCatalog.SubjectReadPermission(p.Type)))
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Not allowed to open this attachment",
                detail: $"Opening an attachment on a {p.Type.ToString().ToLowerInvariant()} needs {ChoRolePermissions.DocumentsRead} and {HttpDocumentCatalog.SubjectReadPermission(p.Type)}.");
        }

        Stream? stream;
        try
        {
            stream = await _catalog.DownloadAsync(tenantId, _actor.UserId, attachment.DocumentId, p.Type, p.Id, HttpContext.RequestAborted);
        }
        catch (DocumentCatalogException ex)
        {
            _logger.LogError(ex, "Could not read an attachment from the document catalog (trace id {TraceId})",
                ChoProblemResults.TraceId(HttpContext));
            return CatalogFailure(ex, "read the attachment file");
        }

        if (stream is null)
        {
            return NotFound("File not found in storage");
        }

        var contentType = AttachmentUploadRules.ContentTypeFor(attachment.DocumentFormat);
        var fileName    = ClaimAttachmentRules.SanitizePathSegment(attachment.Id) + AttachmentUploadRules.ExtensionFor(attachment.DocumentFormat);

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(stream, contentType, fileName);
    }

    /// <summary>
    /// The catalog's answer, passed on where the caller can act on it: 403
    /// (not allowed), 409 (not openable yet, e.g. not cleared by a scan), 413
    /// (too large). Anything else is the catalog being unavailable: 502.
    /// </summary>
    private ObjectResult CatalogFailure(DocumentCatalogException ex, string what) => ex.StatusCode switch
    {
        System.Net.HttpStatusCode.Forbidden => Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "Not allowed", detail: $"The document catalog did not allow this: could not {what}."),
        System.Net.HttpStatusCode.Conflict => Problem(statusCode: StatusCodes.Status409Conflict,
            title: "Not available yet", detail: $"The document catalog can't serve this file yet (for example, it hasn't cleared scanning): could not {what}."),
        System.Net.HttpStatusCode.RequestEntityTooLarge => Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "Attachment too large", detail: $"The document catalog refused the file's size: could not {what}."),
        _ => (ObjectResult)this.ServerError($"Could not {what}. Retry later.", StatusCodes.Status502BadGateway),
    };

    /// <summary>
    /// Generate acknowledgment (999 or 824) for an attachment based on trading partner config
    /// </summary>
    [HttpPost("{id}/acknowledgment")]
    public async Task<ActionResult<AcknowledgmentResponse>> GenerateAcknowledgment(
        string id,
        [FromQuery] bool autoSend = false)
    {
        var tenantId = _actor.TenantId;

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
            attachment.LastUpdatedBy = _actor.UserId;
            
            if (autoSend || tradingPartner?.AutoSendAcknowledgments == true)
            {
                attachment.AcknowledgmentSent = true;
                attachment.AcknowledgmentSentDate = DateTime.UtcNow;
            }

            var updated = await _repository.UpdateAsync(attachment);

            _logger.LogInformation(
                "Generated {AckType} acknowledgment for attachment {AttachmentId}",
                ackType,
                LogSanitizer.Sanitize(id));

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
        catch (TradingPartnerLookupException ex)
        {
            // Never an acknowledgment built from default interchange ids because the lookup failed.
            _logger.LogError(ex, "Trading partner configuration unavailable for attachment {AttachmentId}", LogSanitizer.Sanitize(id));
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Trading partner configuration is unavailable; no acknowledgment was generated. Retry later." });
        }
        catch (AttachmentConcurrencyException)
        {
            // Changed since it was read: 409 (ConcurrencyConflictMiddleware), not 500.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating acknowledgment for attachment {AttachmentId} (trace id {TraceId})",
                LogSanitizer.Sanitize(id), HttpContext.TraceIdentifier);
            return StatusCode(500, new { error = "Failed to generate acknowledgment", traceId = HttpContext.TraceIdentifier });
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
    // No TenantId or submitter: both come from the caller's token.
    public string? ClaimId { get; set; }
    public string? AuthorizationId { get; set; }
    public string? AppealId { get; set; }
    public string? RFAIReference { get; set; }

    /// <summary>
    /// The 275's TRN02: the attachment control number from the claim's PWK06.
    /// With no ClaimId, AuthorizationId or AppealId, the attachment is filed on
    /// the claim that announced it, or held until one does.
    /// </summary>
    public string? AttachmentControlNumber { get; set; }
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
