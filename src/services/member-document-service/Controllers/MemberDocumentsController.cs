using Azure;
using CloudHealthOffice.Infrastructure.Security;
using MemberDocumentService.Models;
using MemberDocumentService.Repositories;
using MemberDocumentService.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;

namespace MemberDocumentService.Controllers;

/// <summary>
/// Member documents are PHI. Every action runs under a CHO token: the tenant
/// and the uploader come from the token, reads need members:read and writes
/// need members:write (the defaults set in Program.cs). Documents are looked up
/// by (token tenant, id), and blob paths start with the token tenant, so no id,
/// path or link reaches another tenant's documents. Content is streamed through
/// <c>/content</c>; this service issues no read links.
///
/// Legal holds: placing or releasing one (including uploading a document
/// already under hold) needs records:legal-hold, not members:write. A held
/// document is immutable: no endpoint deletes it, modifies its record or
/// replaces its content while the hold stands (finalize of an already
/// finalized document answers 409). A release needs a reason. Every placement
/// and release is kept in the document's LegalHoldHistory and written to the
/// audit log with the actor and the reason.
/// </summary>
[ApiController]
public class MemberDocumentsController : ControllerBase
{
    private const string DefaultContainer = "member-documents";
    private static readonly TimeSpan UploadSasLifetime = TimeSpan.FromMinutes(15);

    private string TenantId => _actor.TenantId;

    private readonly IMemberDocumentRepository _repository;
    private readonly IMemberDocumentBlobService _blobService;
    private readonly IRetentionPolicyService _retentionPolicyService;
    private readonly ICurrentActor _actor;
    private readonly MemberDocumentUploadPolicy _uploadPolicy;
    private readonly ILogger<MemberDocumentsController> _logger;

    public MemberDocumentsController(
        IMemberDocumentRepository repository,
        IMemberDocumentBlobService blobService,
        IRetentionPolicyService retentionPolicyService,
        ICurrentActor actor,
        MemberDocumentUploadPolicy uploadPolicy,
        ILogger<MemberDocumentsController> logger)
    {
        _repository = repository;
        _blobService = blobService;
        _retentionPolicyService = retentionPolicyService;
        _actor = actor;
        _uploadPolicy = uploadPolicy;
        _logger = logger;
    }

    /// <summary>
    /// Pre-signed upload: returns a write-only SAS URL for one staging blob.
    /// Its own action because a JSON body never reached the old shared
    /// [FromForm] action (form binding rejected it with 400).
    /// </summary>
    [HttpPost("api/v1/member-documents")]
    [Consumes("application/json")]
    public Task<IActionResult> CreatePresignedUpload([FromBody] PresignedUploadRequest request, CancellationToken ct)
        => CreatePresignedUploadAsync(request, ct);

    [HttpPost("api/v1/member-documents")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> CreateMemberDocument(
        [FromForm] CreateMemberDocumentRequest? formRequest,
        [FromForm] IFormFile? file,
        CancellationToken ct)
    {
        if (formRequest == null)
        {
            return BadRequest("Document metadata is required.");
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest("A file is required for multipart uploads.");
        }

        if (!MemberDocumentUploadPolicy.IsSafePathSegment(formRequest.MemberId))
        {
            return BadRequest("MemberId may contain only letters, digits, '.', '_' and '-'.");
        }

        if (formRequest.LegalHold && !CanManageLegalHolds())
        {
            return LegalHoldForbidden();
        }

        if (file.Length > _uploadPolicy.MaxUploadBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Documents are limited to {_uploadPolicy.MaxUploadBytes} bytes.");
        }

        var contentType = MemberDocumentUploadPolicy.NormalizeContentType(file.ContentType);
        if (contentType == null || !_uploadPolicy.IsAllowedContentType(contentType))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                $"Content type '{file.ContentType}' is not accepted. Allowed: {string.Join(", ", _uploadPolicy.AllowedContentTypes)}.");
        }

        var retention = _retentionPolicyService.ResolvePolicy(
            formRequest.StateCode,
            formRequest.CoverageTerminationDate,
            formRequest.RetentionPolicyId);

        var id = Guid.NewGuid().ToString();
        var blobPath = MemberDocumentUploadPolicy.BuildBlobPath(TenantId, formRequest.MemberId, id, contentType, file.FileName);

        await using var stream = file.OpenReadStream();
        if (!await HasDeclaredSignatureAsync(stream, contentType, ct))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                $"The file content is not {contentType}.");
        }

        var hash = await ComputeSha256Async(stream, ct);
        stream.Position = 0;

        var tags = BuildLifecycleTags(retention.PolicyId, retention.RetentionUntilDate, formRequest.LegalHold);

        var sizeBytes = await _blobService.UploadAsync(
            DefaultContainer,
            blobPath,
            stream,
            contentType,
            tags,
            ct);

        var document = new MemberDocument
        {
            Id = id,
            TenantId = TenantId,
            MemberId = formRequest.MemberId,
            Category = formRequest.Category,
            Subcategory = formRequest.Subcategory,
            Source = formRequest.Source,
            EffectiveDate = formRequest.EffectiveDate,
            ExpirationDate = formRequest.ExpirationDate,
            RetentionPolicyId = retention.PolicyId,
            RetentionUntilDate = retention.RetentionUntilDate,
            RelatedMemberIds = formRequest.RelatedMemberIds ?? new List<string>(),
            LinkedResources = formRequest.LinkedResources ?? new List<string>(),
            BlobContainer = DefaultContainer,
            BlobPath = blobPath,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            ContentHashSha256 = hash,
            UploadedBy = _actor.UserId,
            UploadedDate = DateTime.UtcNow,
            StateCode = formRequest.StateCode,
            CoverageTerminationDate = formRequest.CoverageTerminationDate
        };
        if (formRequest.LegalHold)
        {
            PlaceHold(document, formRequest.LegalHoldReason);
        }

        var created = await _repository.CreateAsync(document);
        if (created.LegalHold)
        {
            AuditHold(created, LegalHoldAction.Set, created.LegalHoldReason);
        }
        return CreatedAtAction(nameof(GetMemberDocument), new { id = created.Id }, created);
    }

    [HttpGet("api/v1/member-documents/{id}")]
    public async Task<IActionResult> GetMemberDocument(string id)
    {
        var doc = await _repository.GetByIdAsync(TenantId, id);
        if (doc == null)
        {
            return NotFound();
        }

        return Ok(doc);
    }

    [HttpGet("api/v1/member-documents/{id}/content")]
    public async Task<IActionResult> GetMemberDocumentContent(string id, CancellationToken ct)
    {
        var doc = await _repository.GetByIdAsync(TenantId, id);
        if (doc == null)
        {
            return NotFound();
        }

        if (doc.PendingUploadBlobPath != null)
        {
            return Conflict("The upload for this document has not been finalized.");
        }

        var stream = await _blobService.DownloadAsync(doc.BlobContainer, doc.BlobPath, ct);
        var fileName = $"{doc.Id}{Path.GetExtension(doc.BlobPath)}";
        // Always an attachment, never sniffed into something the browser renders.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(stream, doc.ContentType, fileName);
    }

    [HttpGet("api/v1/members/{memberId}/documents")]
    public async Task<IActionResult> ListMemberDocuments(string memberId, [FromQuery] string? category = null)
    {
        var documents = await _repository.ListByMemberIdAsync(TenantId, memberId, category);
        return Ok(documents);
    }

    /// <summary>
    /// Places or releases a legal hold. Needs records:legal-hold (ComplianceOfficer,
    /// TenantAdmin, PlatformAdmin); members:write is not enough. A release must
    /// give a reason. Asking for the state the document is already in changes
    /// nothing and records nothing.
    /// </summary>
    [HttpPut("api/v1/member-documents/{id}/legal-hold")]
    [RequirePermission(ChoRolePermissions.LegalHold)]
    public async Task<IActionResult> UpdateLegalHold(string id, [FromBody] LegalHoldRequest request, CancellationToken ct)
    {
        var reason = NormalizeReason(request.Reason);
        if (!request.LegalHold && reason == null)
        {
            return BadRequest("A reason is required to release a legal hold.");
        }

        var doc = await _repository.GetByIdAsync(TenantId, id);
        if (doc == null)
        {
            return NotFound();
        }

        if (doc.LegalHold == request.LegalHold)
        {
            return Ok(doc);
        }

        var action = request.LegalHold ? LegalHoldAction.Set : LegalHoldAction.Released;
        if (request.LegalHold)
        {
            PlaceHold(doc, reason);
        }
        else
        {
            ReleaseHold(doc, reason!);
        }

        MemberDocument updated;
        try
        {
            updated = await _repository.UpdateAsync(doc);
        }
        catch (MemberDocumentConcurrencyException)
        {
            return ConcurrencyConflict();
        }
        AuditHold(updated, action, reason);

        if (doc.PendingUploadBlobPath != null)
        {
            // No final blob yet; finalize applies the tags from the record.
            return Ok(updated);
        }

        var retention = _retentionPolicyService.ResolvePolicy(doc.StateCode, doc.CoverageTerminationDate, doc.RetentionPolicyId);
        var tags = BuildLifecycleTags(retention.PolicyId, retention.RetentionUntilDate, request.LegalHold);
        await _blobService.SetTagsAsync(doc.BlobContainer, doc.BlobPath, tags, ct);

        return Ok(updated);
    }

    /// <summary>
    /// Finalizes a pre-signed upload by applying blob lifecycle tags (retention/legalHold)
    /// and updating the DB record with the actual blob size.  Call this endpoint after the
    /// client has completed the direct-to-blob PUT using the SAS URL returned by the
    /// pre-signed upload flow.
    /// The SAS URL only reaches a staging blob. Finalize reads it, checks the
    /// size limit and that the bytes match the declared content type, hashes it,
    /// writes it to the document's real path and deletes the staging blob. A
    /// rejected upload is deleted and the document stays undownloadable.
    /// </summary>
    [HttpPost("api/v1/member-documents/{id}/finalize")]
    public async Task<IActionResult> FinalizeUpload(string id, CancellationToken ct)
    {
        var doc = await _repository.GetByIdAsync(TenantId, id);
        if (doc == null)
        {
            return NotFound();
        }

        if (doc.PendingUploadBlobPath != null)
        {
            // The document's first content; a hold placed at creation still
            // applies once it is written.
            return await FinalizePendingUploadAsync(doc, ct);
        }

        if (doc.LegalHold)
        {
            // Already finalized: re-finalizing would rewrite its record from
            // the blob. A held document is not changed.
            return LegalHoldConflict();
        }

        // Apply lifecycle tags that were deferred because the blob didn't exist yet.
        var retention = _retentionPolicyService.ResolvePolicy(doc.StateCode, doc.CoverageTerminationDate, doc.RetentionPolicyId);
        var tags = BuildLifecycleTags(retention.PolicyId, retention.RetentionUntilDate, doc.LegalHold);
        await _blobService.SetTagsAsync(doc.BlobContainer, doc.BlobPath, tags, ct);

        // Sync the blob size into the metadata record.
        doc.SizeBytes = await _blobService.GetBlobSizeAsync(doc.BlobContainer, doc.BlobPath, ct);
        MemberDocument updated;
        try
        {
            updated = await _repository.UpdateAsync(doc);
        }
        catch (MemberDocumentConcurrencyException)
        {
            return ConcurrencyConflict();
        }

        return Ok(updated);
    }

    [HttpGet("api/v1/members/{memberId}/fhir/DocumentReference")]
    public async Task<IActionResult> GetDocumentReferences(string memberId, [FromQuery] string? category = null)
    {
        var docs = await _repository.ListByMemberIdAsync(TenantId, memberId, category);

        var entries = docs.Select(d => new
        {
            resource = new
            {
                resourceType = "DocumentReference",
                id = d.Id,
                status = "current",
                type = new
                {
                    text = string.IsNullOrWhiteSpace(d.Subcategory) ? d.Category : $"{d.Category}/{d.Subcategory}"
                },
                subject = new
                {
                    reference = $"Patient/{d.MemberId}"
                },
                date = d.UploadedDate,
                content = new[]
                {
                    new
                    {
                        attachment = new
                        {
                            contentType = d.ContentType,
                            url = $"/api/v1/member-documents/{d.Id}/content",
                            title = d.Category,
                            size = d.SizeBytes,
                            // FHIR R4 Attachment.hash is base64Binary; we store SHA-256 as hex
                            // and convert here. The digest algorithm is SHA-256.
                            hash = ConvertHexHashToBase64(d.ContentHashSha256)
                        }
                    }
                },
                context = new
                {
                    related = d.LinkedResources.Select(link => new { reference = link }).ToArray()
                }
            }
        }).ToArray();

        var bundle = new
        {
            resourceType = "Bundle",
            type = "searchset",
            total = entries.Length,
            entry = entries
        };

        return Ok(bundle);
    }

    private async Task<IActionResult> FinalizePendingUploadAsync(MemberDocument doc, CancellationToken ct)
    {
        var stagingPath = doc.PendingUploadBlobPath!;
        using var buffer = new MemoryStream();
        try
        {
            await using var staged = await _blobService.DownloadAsync(doc.BlobContainer, stagingPath, ct);
            var chunk = new byte[81920];
            int read;
            while ((read = await staged.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > _uploadPolicy.MaxUploadBytes)
                {
                    await _blobService.DeleteIfExistsAsync(doc.BlobContainer, stagingPath, ct);
                    return StatusCode(StatusCodes.Status413PayloadTooLarge,
                        $"Documents are limited to {_uploadPolicy.MaxUploadBytes} bytes; the upload was discarded.");
                }
                buffer.Write(chunk, 0, read);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return Conflict("Nothing has been uploaded for this document yet.");
        }

        if (buffer.Length == 0
            || !MemberDocumentUploadPolicy.MatchesSignature(doc.ContentType, buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 16))))
        {
            await _blobService.DeleteIfExistsAsync(doc.BlobContainer, stagingPath, ct);
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                $"The uploaded content is not {doc.ContentType}; the upload was discarded.");
        }

        buffer.Position = 0;
        var hash = await ComputeSha256Async(buffer, ct);
        buffer.Position = 0;

        var retention = _retentionPolicyService.ResolvePolicy(doc.StateCode, doc.CoverageTerminationDate, doc.RetentionPolicyId);
        var tags = BuildLifecycleTags(retention.PolicyId, retention.RetentionUntilDate, doc.LegalHold);
        doc.SizeBytes = await _blobService.UploadAsync(doc.BlobContainer, doc.BlobPath, buffer, doc.ContentType, tags, ct);
        doc.ContentHashSha256 = hash;
        doc.PendingUploadBlobPath = null;
        MemberDocument updated;
        try
        {
            updated = await _repository.UpdateAsync(doc);
        }
        catch (MemberDocumentConcurrencyException)
        {
            return ConcurrencyConflict();
        }

        await _blobService.DeleteIfExistsAsync(doc.BlobContainer, stagingPath, ct);
        return Ok(updated);
    }

    private async Task<IActionResult> CreatePresignedUploadAsync(PresignedUploadRequest request, CancellationToken ct)
    {
        if (!TryValidateModel(request))
        {
            return ValidationProblem(ModelState);
        }

        if (!MemberDocumentUploadPolicy.IsSafePathSegment(request.MemberId))
        {
            return BadRequest("MemberId may contain only letters, digits, '.', '_' and '-'.");
        }

        if (request.LegalHold && !CanManageLegalHolds())
        {
            return LegalHoldForbidden();
        }

        var contentType = MemberDocumentUploadPolicy.NormalizeContentType(request.ContentType);
        if (contentType == null || !_uploadPolicy.IsAllowedContentType(contentType))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                $"Content type '{request.ContentType}' is not accepted. Allowed: {string.Join(", ", _uploadPolicy.AllowedContentTypes)}.");
        }

        var retention = _retentionPolicyService.ResolvePolicy(
            request.StateCode,
            request.CoverageTerminationDate,
            request.RetentionPolicyId);

        var documentId = Guid.NewGuid().ToString();
        var blobPath = MemberDocumentUploadPolicy.BuildBlobPath(TenantId, request.MemberId, documentId, contentType, request.FileName);
        var stagingPath = $"{blobPath}.upload";
        var expires = DateTimeOffset.UtcNow.Add(UploadSasLifetime);

        var uploadUri = _blobService.GenerateUploadSasUri(DefaultContainer, stagingPath, contentType, expires);
        if (uploadUri == null)
        {
            return StatusCode(StatusCodes.Status501NotImplemented,
                "Pre-signed URL flow requires account-key-backed blob credentials.");
        }

        var document = new MemberDocument
        {
            Id = documentId,
            TenantId = TenantId,
            MemberId = request.MemberId,
            Category = request.Category,
            Subcategory = request.Subcategory,
            Source = MemberDocumentSource.Uploaded,
            RetentionPolicyId = retention.PolicyId,
            RetentionUntilDate = retention.RetentionUntilDate,
            BlobContainer = DefaultContainer,
            BlobPath = blobPath,
            PendingUploadBlobPath = stagingPath,
            ContentType = contentType,
            UploadedBy = _actor.UserId,
            UploadedDate = DateTime.UtcNow,
            StateCode = request.StateCode,
            CoverageTerminationDate = request.CoverageTerminationDate
        };
        if (request.LegalHold)
        {
            PlaceHold(document, request.LegalHoldReason);
        }

        await _repository.CreateAsync(document);
        if (document.LegalHold)
        {
            AuditHold(document, LegalHoldAction.Set, document.LegalHoldReason);
        }

        return Ok(new PresignedUploadResponse
        {
            DocumentId = documentId,
            UploadUrl = uploadUri.ToString(),
            BlobPath = stagingPath,
            ExpiresAtUtc = expires.UtcDateTime
        });
    }

    private bool CanManageLegalHolds() => _actor.HasPermission(ChoRolePermissions.LegalHold);

    private ObjectResult LegalHoldForbidden()
        => StatusCode(StatusCodes.Status403Forbidden,
            $"Placing a legal hold requires {ChoRolePermissions.LegalHold}.");

    /// <summary>
    /// The document changed between this request's read and its save (for
    /// example a legal hold was placed or released meanwhile). Nothing was
    /// saved; the caller reloads and retries.
    /// </summary>
    private ObjectResult ConcurrencyConflict()
        => StatusCode(StatusCodes.Status409Conflict,
            "The document was changed by another request. Reload it and try again.");

    private ObjectResult LegalHoldConflict()
        => StatusCode(StatusCodes.Status409Conflict,
            "The document is under legal hold and cannot be deleted or changed until the hold is released.");

    private void PlaceHold(MemberDocument doc, string? reason)
    {
        reason = NormalizeReason(reason);
        var now = DateTime.UtcNow;
        doc.LegalHold = true;
        doc.LegalHoldSetBy = _actor.UserId;
        doc.LegalHoldSetAt = now;
        doc.LegalHoldReason = reason;
        doc.LegalHoldHistory.Add(new LegalHoldEvent
        {
            Action = LegalHoldAction.Set,
            Actor = _actor.UserId,
            Reason = reason,
            At = now
        });
    }

    private void ReleaseHold(MemberDocument doc, string reason)
    {
        doc.LegalHold = false;
        doc.LegalHoldSetBy = null;
        doc.LegalHoldSetAt = null;
        doc.LegalHoldReason = null;
        doc.LegalHoldHistory.Add(new LegalHoldEvent
        {
            Action = LegalHoldAction.Released,
            Actor = _actor.UserId,
            Reason = reason,
            At = DateTime.UtcNow
        });
    }

    private void AuditHold(MemberDocument doc, LegalHoldAction action, string? reason)
    {
        _logger.LogInformation(
            "AUDIT member document legal hold {Action}: {TenantId}/{DocumentId} by {Actor}; reason: {Reason}",
            action, ForLog(doc.TenantId), ForLog(doc.Id), ForLog(_actor.UserId), ForLog(reason) ?? "(none)");
    }

    private static string? NormalizeReason(string? reason)
    {
        var trimmed = reason?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>One log line per entry: control characters (newlines) cannot forge another.</summary>
    private static string? ForLog(string? value)
        => value == null ? null : new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static IDictionary<string, string> BuildLifecycleTags(string retentionPolicyId, DateTime retentionUntilDate, bool legalHold)
    {
        return new Dictionary<string, string>
        {
            ["retentionPolicyId"] = retentionPolicyId,
            ["retentionUntilDate"] = retentionUntilDate.ToString("yyyy-MM-dd"),
            ["legalHold"] = legalHold ? "true" : "false"
        };
    }

    private static async Task<bool> HasDeclaredSignatureAsync(Stream stream, string contentType, CancellationToken ct)
    {
        var prefix = new byte[16];
        var total = 0;
        int read;
        while (total < prefix.Length && (read = await stream.ReadAsync(prefix.AsMemory(total), ct)) > 0)
        {
            total += read;
        }
        stream.Position = 0;
        return MemberDocumentUploadPolicy.MatchesSignature(contentType, prefix.AsSpan(0, total));
    }

    private async Task<string> ComputeSha256Async(Stream stream, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ConvertHexHashToBase64(string? hexHash)
    {
        if (string.IsNullOrEmpty(hexHash))
        {
            return null;
        }

        try
        {
            return Convert.ToBase64String(Convert.FromHexString(hexHash));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
