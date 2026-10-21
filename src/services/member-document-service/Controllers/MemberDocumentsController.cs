using System.Net;
using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using MemberDocumentService.Models;
using MemberDocumentService.Repositories;
using MemberDocumentService.Services;
using Microsoft.AspNetCore.Mvc;

namespace MemberDocumentService.Controllers;

/// <summary>
/// Member documents are PHI. Every action runs under a CHO token: the tenant
/// and the uploader come from the token. Records are looked up by (token
/// tenant, id), so no id reaches another tenant's documents.
///
/// The files live in the document catalog (document-service), linked to
/// <c>Member:{memberId}</c> and to each related member. This service keeps the
/// member-specific record (category, effective and expiration dates, state,
/// related members and resources) and calls the catalog with its own service
/// token, naming the user in <c>X-CHO-On-Behalf-Of</c>. The catalog trusts
/// that token, so the catalog's rule is applied here in the user's name:
/// reading needs members:read and documents:read, filing needs members:write,
/// members:read and documents:write. Content is streamed through
/// <c>/content</c>, from the catalog; this service issues no read links.
///
/// Legal holds: placing or releasing one (including uploading a document
/// already under hold) needs records:legal-hold. The hold that is enforced is
/// the catalog's; this record mirrors it, with its history. A held document is
/// not changed: finalize of an already finalized held document answers 409. A
/// release needs a reason.
/// </summary>
[ApiController]
public class MemberDocumentsController : ControllerBase
{
    private const string MemberRead = "members:read";

    private string TenantId => _actor.TenantId;

    private readonly IMemberDocumentRepository _repository;
    private readonly IDocumentCatalog _catalog;
    private readonly IRetentionPolicyService _retentionPolicyService;
    private readonly ICurrentActor _actor;
    private readonly MemberDocumentUploadPolicy _uploadPolicy;
    private readonly ILogger<MemberDocumentsController> _logger;

    public MemberDocumentsController(
        IMemberDocumentRepository repository,
        IDocumentCatalog catalog,
        IRetentionPolicyService retentionPolicyService,
        ICurrentActor actor,
        MemberDocumentUploadPolicy uploadPolicy,
        ILogger<MemberDocumentsController> logger)
    {
        _repository = repository;
        _catalog = catalog;
        _retentionPolicyService = retentionPolicyService;
        _actor = actor;
        _uploadPolicy = uploadPolicy;
        _logger = logger;
    }

    /// <summary>
    /// Pre-signed upload: returns a write-only upload link for one staging
    /// object in the catalog's storage. The client PUTs the bytes to UploadUrl
    /// with every UploadHeaders entry, then calls finalize.
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

        if (ValidateMetadata(formRequest.MemberId, formRequest.Category, formRequest.RelatedMemberIds,
                formRequest.StateCode, formRequest.RetentionPolicyId) is { } invalid)
        {
            return invalid;
        }

        if (MissingRights(write: true) is { } forbidden)
        {
            return forbidden;
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
                $"Content type '{LogSanitizer.Sanitize(file.ContentType)}' is not accepted. Allowed: {string.Join(", ", _uploadPolicy.AllowedContentTypes)}.");
        }

        await using var stream = file.OpenReadStream();
        if (!await HasDeclaredSignatureAsync(stream, contentType, ct))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                $"The file content is not {contentType}.");
        }

        var hash = await ComputeSha256Async(stream, ct);
        stream.Position = 0;

        var retention = _retentionPolicyService.ResolvePolicy(
            formRequest.StateCode,
            formRequest.CoverageTerminationDate,
            formRequest.RetentionPolicyId);

        var document = new MemberDocument
        {
            TenantId = TenantId,
            MemberId = formRequest.MemberId,
            Category = formRequest.Category.Trim(),
            Subcategory = formRequest.Subcategory,
            Source = formRequest.Source,
            EffectiveDate = formRequest.EffectiveDate,
            ExpirationDate = formRequest.ExpirationDate,
            RetentionPolicyId = retention.PolicyId,
            RetentionUntilDate = retention.RetentionUntilDate,
            RelatedMemberIds = RelatedMembers(formRequest.MemberId, formRequest.RelatedMemberIds),
            LinkedResources = formRequest.LinkedResources ?? new List<string>(),
            ContentType = contentType,
            SizeBytes = file.Length,
            ContentHashSha256 = hash,
            UploadedBy = _actor.UserId,
            UploadedDate = DateTime.UtcNow,
            StateCode = NormalizeState(formRequest.StateCode),
            CoverageTerminationDate = formRequest.CoverageTerminationDate,
            // Until the catalog has the file, the record can't be opened.
            UploadPending = true
        };
        if (formRequest.LegalHold)
        {
            PlaceHold(document, formRequest.LegalHoldReason);
        }

        // The record is saved first, so a file in the catalog always has a
        // record pointing at it; if filing fails, the record is removed.
        var created = await _repository.CreateAsync(document);

        CatalogDocumentInfo stored;
        try
        {
            stored = await _catalog.UploadAsync(new CatalogUpload(
                TenantId,
                _actor.UserId,
                MemberLinks(created),
                MemberDocumentUploadPolicy.CatalogTypeFor(created.Category),
                Title(created),
                stream,
                contentType,
                MemberDocumentUploadPolicy.FileNameFor(created.Id, contentType, file.FileName),
                Channel(created.Source),
                created.StateCode,
                created.CoverageTerminationDate,
                created.RetentionPolicyId,
                created.LegalHold,
                created.LegalHoldReason,
                new Dictionary<string, string> { ["MemberDocumentId"] = created.Id }), ct);
        }
        catch (DocumentCatalogException ex)
        {
            await _repository.DeleteUnfiledAsync(TenantId, created.Id);
            _logger.LogError(ex, "Could not file a member document in the document catalog");
            return CatalogFailure(ex, "store the document; nothing was saved");
        }

        created.DocumentId = stored.DocumentId;
        created.SizeBytes = stored.SizeBytes ?? created.SizeBytes;
        created.ContentHashSha256 = stored.Sha256 ?? created.ContentHashSha256;
        created.UploadPending = false;
        created = await _repository.UpdateAsync(created);

        if (created.LegalHold)
        {
            AuditHold(created, LegalHoldAction.Set, created.LegalHoldReason);
        }
        return CreatedAtAction(nameof(GetMemberDocument), new { id = created.Id }, created);
    }

    [HttpGet("api/v1/member-documents/{id}")]
    public async Task<IActionResult> GetMemberDocument(string id)
    {
        if (MissingRights(write: false) is { } forbidden)
        {
            return forbidden;
        }

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
        if (MissingRights(write: false) is { } forbidden)
        {
            return forbidden;
        }

        var doc = await _repository.GetByIdAsync(TenantId, id);
        if (doc == null)
        {
            return NotFound();
        }

        if (doc.UploadPending || doc.DocumentId == null)
        {
            return Conflict("The upload for this document has not been finalized.");
        }

        Stream? stream;
        try
        {
            stream = await _catalog.DownloadAsync(TenantId, _actor.UserId, doc.DocumentId, doc.MemberId, ct);
        }
        catch (DocumentCatalogException ex)
        {
            _logger.LogError(ex, "Could not read a member document from the document catalog");
            return CatalogFailure(ex, "read the document");
        }

        if (stream == null)
        {
            _logger.LogWarning("Member document {TenantId}/{DocumentId} has a record but the catalog has no such document on its member",
                LogSanitizer.Sanitize(doc.TenantId), LogSanitizer.Sanitize(doc.Id));
            return NotFound();
        }

        // Always an attachment, never sniffed into something the browser renders.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(stream, doc.ContentType, MemberDocumentUploadPolicy.FileNameFor(doc.Id, doc.ContentType));
    }

    [HttpGet("api/v1/members/{memberId}/documents")]
    public async Task<IActionResult> ListMemberDocuments(string memberId, [FromQuery] string? category = null)
    {
        if (MissingRights(write: false) is { } forbidden)
        {
            return forbidden;
        }

        var documents = await _repository.ListByMemberIdAsync(TenantId, memberId, category);
        return Ok(documents);
    }

    /// <summary>
    /// Places or releases a legal hold. Needs records:legal-hold (ComplianceOfficer,
    /// TenantAdmin, PlatformAdmin); members:write is not enough. A release must
    /// give a reason. The catalog's hold is changed first (it is the one that
    /// is enforced), then this record; if saving the record then conflicts,
    /// retrying finishes it. Asking for the state the document is already in
    /// changes nothing.
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

        if (doc.DocumentId == null)
        {
            return Conflict("The document's file has not reached the catalog yet. Try again shortly.");
        }

        try
        {
            if (!await _catalog.SetLegalHoldAsync(TenantId, _actor.UserId, doc.DocumentId, request.LegalHold, reason, ct))
            {
                _logger.LogError("Member document {TenantId}/{DocumentId} points at a catalog document that does not exist",
                    LogSanitizer.Sanitize(doc.TenantId), LogSanitizer.Sanitize(doc.Id));
                return this.ServerError("The document's file is missing from the catalog.", StatusCodes.Status502BadGateway);
            }
        }
        catch (DocumentCatalogException ex)
        {
            _logger.LogError(ex, "Could not change a legal hold in the document catalog");
            return CatalogFailure(ex, "change the legal hold");
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

        return Ok(updated);
    }

    /// <summary>
    /// Finalizes a pre-signed upload: the catalog reads the staged object,
    /// checks its size and that the bytes match the declared content type,
    /// hashes it and stores it; a rejected upload is discarded and the document
    /// stays undownloadable. Only the user who started the upload can finish
    /// it. Finalizing an already finalized document changes nothing (409 when
    /// it is under legal hold).
    /// </summary>
    [HttpPost("api/v1/member-documents/{id}/finalize")]
    public async Task<IActionResult> FinalizeUpload(string id, CancellationToken ct)
    {
        if (MissingRights(write: true) is { } forbidden)
        {
            return forbidden;
        }

        var doc = await _repository.GetByIdAsync(TenantId, id);
        if (doc == null)
        {
            return NotFound();
        }

        if (!doc.UploadPending)
        {
            return doc.LegalHold ? LegalHoldConflict() : Ok(doc);
        }

        if (doc.DocumentId == null)
        {
            return Conflict("The upload has not been started in the catalog.");
        }

        CatalogDocumentInfo stored;
        try
        {
            stored = await _catalog.FinalizeAsync(TenantId, _actor.UserId, doc.DocumentId, ct);
        }
        catch (DocumentCatalogException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound("This upload can be finalized only by the user who started it.");
        }
        catch (DocumentCatalogException ex)
        {
            _logger.LogError(ex, "Could not finalize a member document upload in the document catalog");
            return CatalogFailure(ex, "finalize the upload");
        }

        // Identical content already in the catalog comes back as that document.
        doc.DocumentId = stored.DocumentId;
        doc.SizeBytes = stored.SizeBytes ?? doc.SizeBytes;
        doc.ContentHashSha256 = stored.Sha256 ?? doc.ContentHashSha256;
        doc.UploadPending = false;
        try
        {
            return Ok(await _repository.UpdateAsync(doc));
        }
        catch (MemberDocumentConcurrencyException)
        {
            return ConcurrencyConflict();
        }
    }

    [HttpGet("api/v1/members/{memberId}/fhir/DocumentReference")]
    public async Task<IActionResult> GetDocumentReferences(string memberId, [FromQuery] string? category = null)
    {
        if (MissingRights(write: false) is { } forbidden)
        {
            return forbidden;
        }

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

    private async Task<IActionResult> CreatePresignedUploadAsync(PresignedUploadRequest request, CancellationToken ct)
    {
        if (!TryValidateModel(request))
        {
            return ValidationProblem(ModelState);
        }

        if (ValidateMetadata(request.MemberId, request.Category, null, request.StateCode, request.RetentionPolicyId) is { } invalid)
        {
            return invalid;
        }

        if (MissingRights(write: true) is { } forbidden)
        {
            return forbidden;
        }

        if (request.LegalHold && !CanManageLegalHolds())
        {
            return LegalHoldForbidden();
        }

        var contentType = MemberDocumentUploadPolicy.NormalizeContentType(request.ContentType);
        if (contentType == null || !_uploadPolicy.IsAllowedContentType(contentType))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                $"Content type '{LogSanitizer.Sanitize(request.ContentType)}' is not accepted. Allowed: {string.Join(", ", _uploadPolicy.AllowedContentTypes)}.");
        }

        var retention = _retentionPolicyService.ResolvePolicy(
            request.StateCode,
            request.CoverageTerminationDate,
            request.RetentionPolicyId);

        var document = new MemberDocument
        {
            TenantId = TenantId,
            MemberId = request.MemberId,
            Category = request.Category.Trim(),
            Subcategory = request.Subcategory,
            Source = MemberDocumentSource.Uploaded,
            RetentionPolicyId = retention.PolicyId,
            RetentionUntilDate = retention.RetentionUntilDate,
            ContentType = contentType,
            UploadedBy = _actor.UserId,
            UploadedDate = DateTime.UtcNow,
            StateCode = NormalizeState(request.StateCode),
            CoverageTerminationDate = request.CoverageTerminationDate,
            UploadPending = true
        };
        if (request.LegalHold)
        {
            PlaceHold(document, request.LegalHoldReason);
        }

        var created = await _repository.CreateAsync(document);

        CatalogUploadLink link;
        try
        {
            link = await _catalog.StartUploadAsync(new CatalogPresignedUpload(
                TenantId,
                _actor.UserId,
                MemberLinks(created),
                MemberDocumentUploadPolicy.CatalogTypeFor(created.Category),
                Title(created),
                contentType,
                MemberDocumentUploadPolicy.FileNameFor(created.Id, contentType, request.FileName),
                created.StateCode,
                created.CoverageTerminationDate,
                created.RetentionPolicyId,
                created.LegalHold,
                created.LegalHoldReason), ct);
        }
        catch (DocumentCatalogException ex)
        {
            await _repository.DeleteUnfiledAsync(TenantId, created.Id);
            _logger.LogError(ex, "Could not start a pre-signed member document upload in the document catalog");
            return CatalogFailure(ex, "start the upload; nothing was saved");
        }

        created.DocumentId = link.DocumentId;
        created = await _repository.UpdateAsync(created);
        if (created.LegalHold)
        {
            AuditHold(created, LegalHoldAction.Set, created.LegalHoldReason);
        }

        return Ok(new PresignedUploadResponse
        {
            DocumentId = created.Id,
            UploadUrl = link.UploadUrl,
            UploadMethod = link.UploadMethod,
            UploadHeaders = new Dictionary<string, string>(link.UploadHeaders),
            ExpiresAtUtc = link.ExpiresAtUtc
        });
    }

    /// <summary>
    /// The catalog's rule, in the user's name: documents:read (or write) plus
    /// members:read, the read permission of the subject these documents hang
    /// off. members:read/members:write themselves are enforced by the pipeline.
    /// </summary>
    private ObjectResult? MissingRights(bool write)
    {
        var documents = write ? ChoRolePermissions.DocumentsWrite : ChoRolePermissions.DocumentsRead;
        if (_actor.HasPermission(documents) && _actor.HasPermission(MemberRead))
        {
            return null;
        }

        return StatusCode(StatusCodes.Status403Forbidden,
            $"Member documents need {documents} and {MemberRead}.");
    }

    private IActionResult? ValidateMetadata(string memberId, string category, IEnumerable<string>? relatedMemberIds,
        string? stateCode, string? retentionPolicyId)
    {
        if (!MemberDocumentUploadPolicy.IsSafePathSegment(memberId))
        {
            return BadRequest("MemberId may contain only letters, digits, '.', '_' and '-'.");
        }

        if (string.IsNullOrWhiteSpace(category) || category.Trim().Length > 80)
        {
            return BadRequest("Category is required (at most 80 characters).");
        }

        if (relatedMemberIds?.Any(r => !MemberDocumentUploadPolicy.IsSafePathSegment(r?.Trim())) == true)
        {
            return BadRequest("RelatedMemberIds may contain only letters, digits, '.', '_' and '-'.");
        }

        if (!MemberDocumentUploadPolicy.IsValidStateCode(stateCode))
        {
            return BadRequest("StateCode is a two-letter state code.");
        }

        if (!MemberDocumentUploadPolicy.IsValidRetentionPolicyId(retentionPolicyId))
        {
            return BadRequest("RetentionPolicyId is one identifier, e.g. \"TX-10Y\".");
        }

        return null;
    }

    private static List<string> RelatedMembers(string memberId, IEnumerable<string>? related) =>
        (related ?? [])
            .Select(r => r.Trim())
            .Where(r => r.Length > 0 && r != memberId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The member and every related member: the catalog subjects the document is filed under.</summary>
    private static List<string> MemberLinks(MemberDocument doc) =>
        new[] { doc.MemberId }.Concat(doc.RelatedMemberIds).Distinct(StringComparer.Ordinal).ToList();

    private static string Title(MemberDocument doc) =>
        string.IsNullOrWhiteSpace(doc.Subcategory) ? doc.Category : $"{doc.Category}/{doc.Subcategory.Trim()}";

    private static string Channel(MemberDocumentSource source) => source switch
    {
        MemberDocumentSource.Generated => "Generated",
        MemberDocumentSource.Received => "Mail",
        _ => "Upload"
    };

    private static string? NormalizeState(string? stateCode) =>
        string.IsNullOrWhiteSpace(stateCode) ? null : stateCode.Trim().ToUpperInvariant();

    /// <summary>
    /// The catalog's answer, passed on where the caller can act on it: 403
    /// (not allowed), 409 (not openable yet, e.g. not cleared by scanning, or
    /// nothing uploaded), 413 (too large), 415 (content not accepted). Anything
    /// else is the catalog being unavailable or refusing this service: 502.
    /// </summary>
    private ObjectResult CatalogFailure(DocumentCatalogException ex, string what) => ex.StatusCode switch
    {
        HttpStatusCode.Forbidden => StatusCode(StatusCodes.Status403Forbidden,
            $"The document catalog did not allow this: could not {what}."),
        HttpStatusCode.Conflict => StatusCode(StatusCodes.Status409Conflict,
            $"The document catalog can't do this yet (for example, the file hasn't cleared scanning or hasn't been uploaded): could not {what}."),
        HttpStatusCode.RequestEntityTooLarge => StatusCode(StatusCodes.Status413PayloadTooLarge,
            $"The document catalog refused the file's size: could not {what}."),
        HttpStatusCode.UnsupportedMediaType => StatusCode(StatusCodes.Status415UnsupportedMediaType,
            $"The document catalog refused the file's content: could not {what}."),
        _ => (ObjectResult)this.ServerError($"Could not {what}. Retry later.", StatusCodes.Status502BadGateway),
    };

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
            action, LogSanitizer.Sanitize(doc.TenantId), LogSanitizer.Sanitize(doc.Id), LogSanitizer.Sanitize(_actor.UserId), reason is null ? "(none)" : LogSanitizer.Sanitize(reason));
    }

    private static string? NormalizeReason(string? reason)
    {
        var trimmed = reason?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
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

    private static async Task<string> ComputeSha256Async(Stream stream, CancellationToken ct)
    {
        var hash = await SHA256.HashDataAsync(stream, ct);
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
