using System.Security.Cryptography;
using CloudHealthOffice.DocumentStore;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using DocumentService.Models;
using DocumentService.Repositories;
using DocumentService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocumentService.Controllers;

/// <summary>
/// The document catalog API (docs/architecture/document-store.md §3–§6).
///
/// Every read names the subject it works from (<c>subjectKind</c> and
/// <c>subjectId</c>): "documents for claim X", never "document Y". The caller
/// needs documents:read, the subject's own read permission, a link from the
/// document to that subject, and for sensitive documents
/// documents:read-sensitive (see <see cref="SubjectAccess"/>). A document the
/// caller can't reach through that subject answers 404, so ids reveal nothing.
///
/// Every list, view, download and change is written to the append-only access
/// audit before it is answered, denied attempts included. If the audit can't
/// be written, nothing is served.
/// </summary>
[ApiController]
[Route("api/v1/documents")]
public sealed class DocumentsController : ControllerBase
{
    public const string Container = "documents";
    public const string ScreenHeader = "X-CHO-Screen";

    private readonly IDocumentRepository _documents;
    private readonly IDocumentAccessAudit _audit;
    private readonly IDocumentStore _store;
    private readonly IDocumentScanner _scanner;
    private readonly IRetentionPolicyService _retention;
    private readonly DocumentUploadPolicy _uploadPolicy;
    private readonly DocumentServiceOptions _options;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _clock;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        IDocumentRepository documents,
        IDocumentAccessAudit audit,
        IDocumentStore store,
        IDocumentScanner scanner,
        IRetentionPolicyService retention,
        DocumentUploadPolicy uploadPolicy,
        IOptions<DocumentServiceOptions> options,
        ICurrentActor actor,
        TimeProvider clock,
        ILogger<DocumentsController> logger)
    {
        _documents = documents;
        _audit = audit;
        _store = store;
        _scanner = scanner;
        _retention = retention;
        _uploadPolicy = uploadPolicy;
        _options = options.Value;
        _actor = actor;
        _clock = clock;
        _logger = logger;
    }

    private string TenantId => _actor.TenantId;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ── Reading ──────────────────────────────────────────────────────────

    /// <summary>The documents on one subject that this caller may see, plus how many were withheld.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? subjectKind, [FromQuery] string? subjectId, CancellationToken ct)
    {
        if (!SubjectAccess.TryCreate(subjectKind, subjectId, out var subject))
            return BadRequest("subjectKind and subjectId name the subject whose documents are listed.");

        if (!SubjectAccess.CanReadSubject(_actor, subject.Kind))
        {
            var reason = $"needs {SubjectAccess.ReadPermissionFor(subject.Kind)} for {subject.Kind} documents";
            await AuditAsync(DocumentAccessAction.List, null, subject, allowed: false, reason, ct);
            return Forbidden(reason);
        }

        var all = await _documents.ListBySubjectAsync(TenantId, subject, ct);
        var visible = all.Where(d => SubjectAccess.DecideSensitivity(_actor, d).Allowed).ToList();
        var withheld = all.Count - visible.Count;

        await AuditAsync(DocumentAccessAction.List, null, subject, allowed: true,
            $"returned {visible.Count}" + (withheld > 0 ? $", withheld {withheld}" : string.Empty), ct);

        return Ok(new DocumentListResponse
        {
            Documents = visible.Select(d => SubjectAccess.Project(_actor, d)).ToList(),
            Withheld = withheld
        });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, [FromQuery] string? subjectKind, [FromQuery] string? subjectId, CancellationToken ct)
    {
        var (document, denied) = await OpenAsync(id, subjectKind, subjectId, DocumentAccessAction.View, ct);
        return denied ?? Ok(SubjectAccess.Project(_actor, document!));
    }

    /// <summary>
    /// Streams the content, always as an attachment. Pending (unscanned)
    /// content is served only where Documents:AllowUnscannedContent is on;
    /// Infected and Failed never are.
    /// </summary>
    [HttpGet("{id}/content")]
    public async Task<IActionResult> Content(string id, [FromQuery] string? subjectKind, [FromQuery] string? subjectId, CancellationToken ct)
    {
        if (!SubjectAccess.TryCreate(subjectKind, subjectId, out var subject))
            return BadRequest("subjectKind and subjectId name the subject the document is opened from.");

        var document = await _documents.GetAsync(TenantId, id, ct);
        var decision = document is null
            ? AccessDecision.Deny("no such document in tenant")
            : SubjectAccess.Decide(_actor, document, subject);

        if (decision.Allowed && document!.PendingUploadPath != null)
            decision = AccessDecision.Deny("upload not finalized");
        if (decision.Allowed && !ScanAllowsServing(document!.Scan.Status))
            decision = AccessDecision.Deny($"scan status {document.Scan.Status}");

        await AuditAsync(DocumentAccessAction.Download, id, subject, decision.Allowed, decision.Reason, ct);
        if (!decision.Allowed)
            return DeniedResult(document, subject, decision);

        Stream stream;
        try
        {
            stream = await _store.DownloadAsync(document!.StorageContainer, document.StoragePath, ct);
        }
        catch (DocumentNotFoundException)
        {
            _logger.LogError("Catalog document {TenantId}/{DocumentId} has no stored content",
                LogSanitizer.Sanitize(TenantId), LogSanitizer.Sanitize(id));
            return NotFound();
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(stream, document.ContentType, $"{document.Id}{Path.GetExtension(document.StoragePath)}");
    }

    /// <summary>Who opened this document, when, from where, and whether it was allowed.</summary>
    [HttpGet("{id}/access-log")]
    [RequirePermission("audit:read")]
    public async Task<IActionResult> AccessLog(string id, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var entries = await _audit.ForDocumentAsync(TenantId, id, Math.Clamp(limit, 1, 1000), ct);
        return Ok(entries);
    }

    // ── Uploading ────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads a file with its metadata. Identical content already in the
    /// tenant is not stored again: the existing document gains this upload's
    /// links and tags, and comes back with 200 instead of 201.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(30L * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] UploadDocumentForm form, IFormFile? file, CancellationToken ct)
    {
        var metadata = ValidateMetadata(form.Type, form.Links, form.Sensitivity, form.SourceChannel, form.ReceivedAt);
        if (metadata.Error is { } error)
            return error;

        if (file is null || file.Length == 0)
            return BadRequest("A file is required.");
        if (file.Length > _uploadPolicy.MaxUploadBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, $"Documents are limited to {_uploadPolicy.MaxUploadBytes} bytes.");

        var contentType = DocumentUploadPolicy.NormalizeContentType(file.ContentType);
        if (contentType is null || !_uploadPolicy.IsAllowedContentType(contentType))
            return UnsupportedType(file.ContentType);

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (!DocumentUploadPolicy.MatchesSignature(contentType, buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 16))))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, $"The file content is not {contentType}.");

        var sha256 = Sha256Hex(buffer);

        var duplicate = await _documents.FindByHashAsync(TenantId, sha256, ct);
        if (duplicate != null)
            return await MergeDuplicateAsync(duplicate, metadata, ct);

        var id = Guid.NewGuid().ToString("N");
        var document = NewDocument(id, form.Type, form.Title, contentType, file.FileName, metadata, form.StateCode, form.RetentionPolicyId);
        document.Sha256 = sha256;
        document.SizeBytes = buffer.Length;

        buffer.Position = 0;
        document.Scan = await _scanner.ScanAsync(buffer, contentType, ct);
        buffer.Position = 0;
        await _store.UploadAsync(Container, document.StoragePath, buffer, contentType, StorageTags(document), ct);

        await _documents.CreateAsync(document, ct);
        await AuditAsync(DocumentAccessAction.Upload, id, document.Links[0], allowed: true,
            $"{document.Type}, {document.Links.Count} link(s)", ct);

        return StatusCode(StatusCodes.Status201Created, SubjectAccess.Project(_actor, document));
    }

    /// <summary>
    /// Starts a pre-signed upload: records the document (pending) and returns
    /// a short-lived, write-only link for one staging object. The client PUTs
    /// the bytes with every header in UploadHeaders, then calls finalize.
    /// </summary>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> StartUpload([FromBody] PresignedUploadRequest request, CancellationToken ct)
    {
        var metadata = ValidateMetadata(request.Type, request.Links, request.Sensitivity, request.SourceChannel, request.ReceivedAt);
        if (metadata.Error is { } error)
            return error;

        var contentType = DocumentUploadPolicy.NormalizeContentType(request.ContentType);
        if (contentType is null || !_uploadPolicy.IsAllowedContentType(contentType))
            return UnsupportedType(request.ContentType);

        var id = Guid.NewGuid().ToString("N");
        var document = NewDocument(id, request.Type, request.Title, contentType, request.FileName, metadata, request.StateCode, request.RetentionPolicyId);
        document.PendingUploadPath = $"{document.StoragePath}.upload";

        var expires = _clock.GetUtcNow().Add(_options.UploadLinkLifetime);
        var link = await _store.CreateUploadLinkAsync(Container, document.PendingUploadPath, contentType, expires, ct);
        if (link is null)
        {
            return StatusCode(StatusCodes.Status501NotImplemented,
                "Pre-signed uploads need storage credentials that can sign upload links; upload the file with multipart/form-data instead.");
        }

        await _documents.CreateAsync(document, ct);

        return Ok(new PresignedUploadResponse
        {
            DocumentId = id,
            UploadUrl = link.Url.ToString(),
            UploadMethod = link.Method,
            UploadHeaders = new Dictionary<string, string>(link.Headers),
            ExpiresAtUtc = link.ExpiresAtUtc.UtcDateTime
        });
    }

    /// <summary>
    /// Finishes a pre-signed upload. Only the uploader may finalize. The staged
    /// object is size-checked before it is read, then checked against the
    /// declared type, hashed and scanned, and copied to the document's path,
    /// which no upload link covers. A duplicate of an existing document merges
    /// into it.
    /// </summary>
    [HttpPost("{id}/finalize")]
    public async Task<IActionResult> Finalize(string id, CancellationToken ct)
    {
        var document = await _documents.GetAsync(TenantId, id, ct);
        if (document is null || document.PendingUploadPath is null || document.CreatedBy != _actor.UserId)
            return NotFound();

        var staging = document.PendingUploadPath;

        DocumentProperties staged;
        try
        {
            staged = await _store.GetPropertiesAsync(Container, staging, ct);
        }
        catch (DocumentNotFoundException)
        {
            return Conflict("Nothing has been uploaded for this document yet.");
        }
        if (staged.SizeBytes > _uploadPolicy.MaxUploadBytes)
        {
            await _store.DeleteAsync(Container, staging, ct);
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Documents are limited to {_uploadPolicy.MaxUploadBytes} bytes; the upload was discarded.");
        }

        using var buffer = new MemoryStream();
        await using (var content = await _store.DownloadAsync(Container, staging, ct))
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > _uploadPolicy.MaxUploadBytes)
                {
                    await _store.DeleteAsync(Container, staging, ct);
                    return StatusCode(StatusCodes.Status413PayloadTooLarge,
                        $"Documents are limited to {_uploadPolicy.MaxUploadBytes} bytes; the upload was discarded.");
                }
                buffer.Write(chunk, 0, read);
            }
        }

        if (buffer.Length == 0
            || !DocumentUploadPolicy.MatchesSignature(document.ContentType, buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 16))))
        {
            await _store.DeleteAsync(Container, staging, ct);
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                $"The uploaded content is not {document.ContentType}; the upload was discarded.");
        }

        var sha256 = Sha256Hex(buffer);
        var duplicate = await _documents.FindByHashAsync(TenantId, sha256, ct);
        if (duplicate != null)
        {
            var merged = await MergeDuplicateAsync(duplicate,
                new UploadMetadata(document.Links, document.Sensitivity, document.Source.Channel, document.ReceivedAt, null), ct);
            await _store.DeleteAsync(Container, staging, ct);
            await _documents.DeletePendingAsync(TenantId, document.Id, ct);
            return merged;
        }

        buffer.Position = 0;
        document.Scan = await _scanner.ScanAsync(buffer, document.ContentType, ct);
        buffer.Position = 0;
        document.Sha256 = sha256;
        document.SizeBytes = buffer.Length;
        await _store.UploadAsync(Container, document.StoragePath, buffer, document.ContentType, StorageTags(document), ct);
        document.PendingUploadPath = null;

        try
        {
            document = await _documents.UpdateAsync(document, ct);
        }
        catch (DocumentConcurrencyException)
        {
            return ConcurrencyConflict();
        }

        await _store.DeleteAsync(Container, staging, ct);
        await AuditAsync(DocumentAccessAction.Upload, id, document.Links[0], allowed: true,
            $"{document.Type}, {document.Links.Count} link(s), pre-signed", ct);
        return Ok(SubjectAccess.Project(_actor, document));
    }

    // ── Links ────────────────────────────────────────────────────────────

    /// <summary>Links the document to another subject. The caller works from a subject it is already linked to, and must be able to read the new one.</summary>
    [HttpPost("{id}/links")]
    public async Task<IActionResult> AddLink(string id, [FromQuery] string? subjectKind, [FromQuery] string? subjectId,
        [FromBody] LinkRequest request, CancellationToken ct)
    {
        if (!SubjectAccess.TryParseLink(request.Link, out var link))
            return BadRequest("Link is \"Kind:Id\", e.g. \"Appeal:A-123\".");

        var (document, denied) = await OpenAsync(id, subjectKind, subjectId, DocumentAccessAction.Link, ct, detail: $"add {link}");
        if (denied != null)
            return denied;

        if (!SubjectAccess.CanReadSubject(_actor, link.Kind))
            return Forbidden($"needs {SubjectAccess.ReadPermissionFor(link.Kind)} to link a document to a {link.Kind}");
        if (document!.Retention.LegalHold.Set)
            return LegalHoldConflict();
        if (document.Links.Contains(link))
            return Ok(SubjectAccess.Project(_actor, document));

        document.Links.Add(link);
        return await SaveAsync(document, ct);
    }

    /// <summary>Removes one link. A document always keeps at least one, and a held document keeps all of them.</summary>
    [HttpDelete("{id}/links/{kind}/{linkId}")]
    public async Task<IActionResult> RemoveLink(string id, string kind, string linkId,
        [FromQuery] string? subjectKind, [FromQuery] string? subjectId, CancellationToken ct)
    {
        if (!SubjectAccess.TryCreate(kind, linkId, out var link))
            return BadRequest("Unknown link.");

        var (document, denied) = await OpenAsync(id, subjectKind, subjectId, DocumentAccessAction.Unlink, ct, detail: $"remove {link}");
        if (denied != null)
            return denied;

        if (!SubjectAccess.CanReadSubject(_actor, link.Kind))
            return Forbidden($"needs {SubjectAccess.ReadPermissionFor(link.Kind)} to unlink a {link.Kind}");
        if (document!.Retention.LegalHold.Set)
            return LegalHoldConflict();
        if (!document.Links.Contains(link))
            return NotFound();
        if (document.Links.Count == 1)
            return Conflict("A document keeps at least one link. Link it elsewhere first.");

        document.Links.Remove(link);
        return await SaveAsync(document, ct);
    }

    // ── Legal hold ───────────────────────────────────────────────────────

    /// <summary>
    /// Places or releases a legal hold (records:legal-hold). A held document's
    /// links can't change; deletion (the retention job, a later step) skips
    /// it. A release needs a reason. Both are kept in the document's history
    /// and the access audit.
    /// </summary>
    [HttpPut("{id}/legal-hold")]
    [RequirePermission(ChoRolePermissions.LegalHold)]
    public async Task<IActionResult> UpdateLegalHold(string id, [FromBody] LegalHoldRequest request, CancellationToken ct)
    {
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (!request.LegalHold && reason is null)
            return BadRequest("A reason is required to release a legal hold.");

        var document = await _documents.GetAsync(TenantId, id, ct);
        if (document is null)
            return NotFound();

        var hold = document.Retention.LegalHold;
        if (hold.Set == request.LegalHold)
            return Ok(SubjectAccess.Project(_actor, document));

        var now = Now;
        hold.Set = request.LegalHold;
        hold.By = request.LegalHold ? _actor.UserId : null;
        hold.At = request.LegalHold ? now : null;
        hold.Reason = request.LegalHold ? reason : null;
        hold.History.Add(new LegalHoldEvent
        {
            Action = request.LegalHold ? LegalHoldAction.Set : LegalHoldAction.Released,
            Actor = _actor.UserId,
            Reason = reason,
            At = now
        });

        try
        {
            document = await _documents.UpdateAsync(document, ct);
        }
        catch (DocumentConcurrencyException)
        {
            return ConcurrencyConflict();
        }

        await AuditAsync(DocumentAccessAction.LegalHold, id, null, allowed: true,
            $"{(request.LegalHold ? "set" : "released")}: {reason ?? "(no reason)"}", ct);

        if (document.PendingUploadPath is null)
            await _store.SetTagsAsync(document.StorageContainer, document.StoragePath, StorageTags(document), ct);

        return Ok(SubjectAccess.Project(_actor, document));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private sealed record UploadMetadata(
        List<DocumentLink> Links, List<string> Sensitivity, string Channel, DateTime ReceivedAt, IActionResult? Error);

    private UploadMetadata ValidateMetadata(string? type, List<string>? links, List<string>? sensitivity, string? channel, DateTime? receivedAt)
    {
        UploadMetadata Fail(IActionResult error) => new([], [], string.Empty, default, error);

        if (!DocumentTypes.IsValid(type))
            return Fail(BadRequest("Type is one identifier (letters, digits, '-' or '_'), e.g. \"MedicalRecord\"."));

        var parsed = new List<DocumentLink>();
        foreach (var value in links ?? [])
        {
            if (!SubjectAccess.TryParseLink(value, out var link))
                return Fail(BadRequest($"Link '{LogSanitizer.Sanitize(value)}' is not \"Kind:Id\"."));
            if (!parsed.Contains(link))
                parsed.Add(link);
        }
        if (parsed.Count == 0)
            return Fail(BadRequest("A document is linked to at least one subject, e.g. \"Claim:CLM-1\"."));

        foreach (var link in parsed)
        {
            if (!SubjectAccess.CanReadSubject(_actor, link.Kind))
                return Fail(Forbidden($"needs {SubjectAccess.ReadPermissionFor(link.Kind)} to file a document on a {link.Kind}"));
        }

        var tags = (sensitivity ?? []).Select(t => t.Trim().ToUpperInvariant()).Distinct().ToList();
        if (tags.FirstOrDefault(t => !SensitivityTags.All.Contains(t)) is { } unknown)
            return Fail(BadRequest($"Unknown sensitivity tag '{LogSanitizer.Sanitize(unknown)}'."));

        var sourceChannel = string.IsNullOrWhiteSpace(channel) ? DocumentChannels.Upload : channel.Trim();
        if (!DocumentChannels.All.Contains(sourceChannel))
            return Fail(BadRequest($"Unknown source channel '{LogSanitizer.Sanitize(sourceChannel)}'."));

        var now = Now;
        var received = receivedAt?.ToUniversalTime() ?? now;
        if (received > now.AddMinutes(5))
            return Fail(BadRequest("ReceivedAt is when the document arrived; it can't be in the future."));

        return new UploadMetadata(parsed, tags, sourceChannel, received, null);
    }

    private CatalogDocument NewDocument(string id, string type, string? title, string contentType, string? fileName,
        UploadMetadata metadata, string? stateCode, string? retentionPolicyId)
    {
        if (!DocumentUploadPolicy.IsSafePathSegment(TenantId))
            throw new InvalidOperationException("Tenant id cannot be used in a storage path.");

        var policy = _retention.ResolvePolicy(stateCode, null, retentionPolicyId);
        return new CatalogDocument
        {
            Id = id,
            TenantId = TenantId,
            Type = type.Trim(),
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            ContentType = contentType,
            Source = new DocumentSource { Channel = metadata.Channel },
            Links = metadata.Links,
            Sensitivity = metadata.Sensitivity,
            Retention = new DocumentRetention { PolicyId = policy.PolicyId, RetainUntil = policy.RetentionUntilDate },
            StorageContainer = Container,
            StoragePath = DocumentUploadPolicy.BuildStoragePath(TenantId, id, contentType, fileName),
            CreatedBy = _actor.UserId,
            CreatedAt = Now,
            ReceivedAt = metadata.ReceivedAt
        };
    }

    /// <summary>Identical content already stored: link the existing document to this upload's subjects and tags instead of storing it again.</summary>
    private async Task<IActionResult> MergeDuplicateAsync(CatalogDocument existing, UploadMetadata metadata, CancellationToken ct)
    {
        if (existing.Retention.LegalHold.Set && metadata.Links.Any(l => !existing.Links.Contains(l)))
            return LegalHoldConflict();

        foreach (var link in metadata.Links.Where(l => !existing.Links.Contains(l)))
            existing.Links.Add(link);
        foreach (var tag in metadata.Sensitivity.Where(t => !existing.Sensitivity.Contains(t)))
            existing.Sensitivity.Add(tag);

        try
        {
            existing = await _documents.UpdateAsync(existing, ct);
        }
        catch (DocumentConcurrencyException)
        {
            return ConcurrencyConflict();
        }

        await AuditAsync(DocumentAccessAction.Upload, existing.Id, metadata.Links[0], allowed: true,
            "duplicate content: linked to the existing document", ct);

        // The uploader holds the bytes, but may still lack the sensitivity
        // permission for what they just tagged: answer with the record only if
        // they may see it.
        return SubjectAccess.DecideSensitivity(_actor, existing).Allowed
            ? Ok(SubjectAccess.Project(_actor, existing))
            : Ok(new { id = existing.Id, duplicate = true });
    }

    /// <summary>Loads a document through a subject, auditing the attempt either way. Returns the document or the response to send.</summary>
    private async Task<(CatalogDocument? Document, IActionResult? Denied)> OpenAsync(
        string id, string? subjectKind, string? subjectId, DocumentAccessAction action, CancellationToken ct, string? detail = null)
    {
        if (!SubjectAccess.TryCreate(subjectKind, subjectId, out var subject))
            return (null, BadRequest("subjectKind and subjectId name the subject the document is opened from."));

        var document = await _documents.GetAsync(TenantId, id, ct);
        var decision = document is null
            ? AccessDecision.Deny("no such document in tenant")
            : SubjectAccess.Decide(_actor, document, subject);

        await AuditAsync(action, id, subject, decision.Allowed,
            decision.Allowed ? detail : decision.Reason, ct);

        return decision.Allowed ? (document, null) : (null, DeniedResult(document, subject, decision));
    }

    /// <summary>
    /// 403 when the caller lacks a permission it could be granted; 404 when the
    /// document is missing or not on that subject, so ids reveal nothing.
    /// </summary>
    private IActionResult DeniedResult(CatalogDocument? document, DocumentLink subject, AccessDecision decision)
    {
        if (!SubjectAccess.CanReadSubject(_actor, subject.Kind))
            return Forbidden(decision.Reason!);
        if (document is null || !document.Links.Contains(subject))
            return NotFound();
        if (document.PendingUploadPath != null)
            return Conflict("The upload for this document has not been finalized.");
        if (!ScanAllowsServing(document.Scan.Status) && SubjectAccess.DecideSensitivity(_actor, document).Allowed)
            return Conflict($"The document can't be opened: scan status {document.Scan.Status}.");
        return Forbidden(decision.Reason!);
    }

    private bool ScanAllowsServing(ScanStatus status) =>
        status == ScanStatus.Clean || (status == ScanStatus.Pending && _options.AllowUnscannedContent);

    private async Task<IActionResult> SaveAsync(CatalogDocument document, CancellationToken ct)
    {
        try
        {
            document = await _documents.UpdateAsync(document, ct);
        }
        catch (DocumentConcurrencyException)
        {
            return ConcurrencyConflict();
        }
        return Ok(SubjectAccess.Project(_actor, document));
    }

    /// <summary>Written before the response; a failure here fails the request, so nothing is served unaudited.</summary>
    private Task AuditAsync(DocumentAccessAction action, string? documentId, DocumentLink? subject, bool allowed, string? detail, CancellationToken ct)
    {
        var screen = Request.Headers[ScreenHeader].ToString();
        return _audit.RecordAsync(new DocumentAccessEvent
        {
            TenantId = TenantId,
            DocumentId = documentId,
            Actor = _actor.UserId,
            At = Now,
            Action = action,
            SubjectKind = subject?.Kind.ToString(),
            SubjectId = subject?.Id,
            Screen = string.IsNullOrWhiteSpace(screen) ? null : LogSanitizer.Sanitize(screen.Length > 64 ? screen[..64] : screen),
            Allowed = allowed,
            Detail = detail
        }, ct);
    }

    private static IReadOnlyDictionary<string, string> StorageTags(CatalogDocument d) => new Dictionary<string, string>
    {
        ["retentionPolicyId"] = d.Retention.PolicyId,
        ["retentionUntilDate"] = d.Retention.RetainUntil.ToString("yyyy-MM-dd"),
        ["legalHold"] = d.Retention.LegalHold.Set ? "true" : "false"
    };

    private static string Sha256Hex(MemoryStream buffer) =>
        Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))).ToLowerInvariant();

    private ObjectResult Forbidden(string reason) => StatusCode(StatusCodes.Status403Forbidden, reason);

    private ObjectResult UnsupportedType(string? given) => StatusCode(StatusCodes.Status415UnsupportedMediaType,
        $"Content type '{LogSanitizer.Sanitize(given)}' is not accepted. Allowed: {string.Join(", ", _uploadPolicy.AllowedContentTypes)}.");

    private ObjectResult ConcurrencyConflict() => StatusCode(StatusCodes.Status409Conflict,
        "The document was changed by another request. Reload it and try again.");

    private ObjectResult LegalHoldConflict() => StatusCode(StatusCodes.Status409Conflict,
        "The document is under legal hold; its links can't change until the hold is released.");
}
