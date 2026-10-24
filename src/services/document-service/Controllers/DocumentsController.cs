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
/// The document catalog API (docs/architecture/document-store.md §3–§6, §11).
///
/// Every read names the subject it works from (<c>subjectKind</c> and
/// <c>subjectId</c>): "documents for claim X", never "document Y". The caller
/// needs documents:read, the subject's own read permission, a link from the
/// document to that subject, and for sensitive documents
/// documents:read-sensitive (see <see cref="SubjectAccess"/>). A document the
/// caller can't reach through that subject answers 404, so ids reveal nothing,
/// and a restricted one answers a 403 that says nothing about why (the reason,
/// e.g. which sensitivity tag, goes only to the audit).
///
/// Every attempt (list, view, download, upload, link change, legal hold),
/// allowed or refused, is written to the append-only access audit before it
/// is answered. If the audit can't be written, nothing is served or changed.
/// </summary>
[ApiController]
[Route("api/v1/documents")]
public sealed class DocumentsController : ControllerBase, Microsoft.AspNetCore.Mvc.Filters.IActionFilter
{
    public const string Container = "documents";
    public const string ScreenHeader = "X-CHO-Screen";

    /// <summary>
    /// The user a CHO service is acting for. Honored only from a service token
    /// (a user can't claim to act for someone else); recorded in the document
    /// and the audit, never used for access decisions.
    /// </summary>
    public const string OnBehalfOfHeader = "X-CHO-On-Behalf-Of";

    private static readonly System.Text.RegularExpressions.Regex ExternalIdKey =
        new("^[A-Za-z][A-Za-z0-9_-]{0,39}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex SafeToken =
        new("^[A-Za-z0-9][A-Za-z0-9_.:@-]{0,127}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The only thing a refused caller learns about a restricted document.</summary>
    public const string RestrictedMessage = "This document is restricted.";

    private readonly IDocumentRepository _documents;
    private readonly IDocumentAccessAudit _audit;
    private readonly IDocumentStore _store;
    private readonly IDocumentScanner _scanner;
    private readonly IRetentionPolicyService _retention;
    private readonly DocumentUploadPolicy _uploadPolicy;
    private readonly DocumentServiceOptions _options;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _clock;
    private readonly ClinicalSummaryReader _clinicalSummary;
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
        ClinicalSummaryReader clinicalSummary,
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
        _clinicalSummary = clinicalSummary;
        _logger = logger;
    }

    private string TenantId => _actor.TenantId;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// A service that names a user must name a usable one: an unreadable value
    /// would otherwise drop out of the audit silently.
    /// </summary>
    [NonAction]
    public void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        if (!_actor.IsService || !Request.Headers.TryGetValue(OnBehalfOfHeader, out var value))
            return;
        if (!SafeToken.IsMatch(value.ToString().Trim()))
            context.Result = BadRequest($"{OnBehalfOfHeader} must be one user id (letters, digits, '_', '.', ':', '@', '-').");
    }

    [NonAction]
    public void OnActionExecuted(Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext context)
    {
    }

    private string? OnBehalfOf
    {
        get
        {
            if (!_actor.IsService) return null;
            var value = Request.Headers[OnBehalfOfHeader].ToString().Trim();
            return SafeToken.IsMatch(value) ? value : null;
        }
    }

    /// <summary>
    /// Who a legal-hold action is recorded against: the user a service acts
    /// for, else the caller. Recording only; access still comes from the token.
    /// </summary>
    private string ActingUser => OnBehalfOf ?? _actor.UserId;

    // ── Reading ──────────────────────────────────────────────────────────

    /// <summary>The documents on one subject that this caller may see, plus how many were withheld.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? subjectKind, [FromQuery] string? subjectId, CancellationToken ct)
    {
        if (!SubjectAccess.TryCreate(subjectKind, subjectId, out var subject))
            return BadRequest("subjectKind and subjectId name the subject whose documents are listed.");

        if (!SubjectAccess.CanReadSubject(_actor, subject.Kind))
        {
            var reason = SubjectPermissionMessage(subject.Kind);
            await AuditAsync(DocumentAccessAction.List, null, subject, allowed: false, reason, ct);
            return Forbidden(reason);
        }

        var all = await _documents.ListBySubjectAsync(TenantId, subject, ct);
        var visible = all.Where(d => SubjectAccess.DecideSensitivity(_actor, d).Allowed).ToList();
        var withheld = all.Where(d => !visible.Contains(d)).Select(d => d.Id).ToList();

        // Which documents were shown, by id, so the audit answers "who saw this record".
        await AuditAsync(DocumentAccessAction.List, null, subject, allowed: true,
            $"returned [{string.Join(",", visible.Select(d => d.Id))}]"
            + (withheld.Count > 0 ? $"; withheld [{string.Join(",", withheld)}]" : string.Empty), ct);

        return Ok(new DocumentListResponse
        {
            Documents = visible.Select(d => SubjectAccess.Project(_actor, d)).ToList(),
            Withheld = withheld.Count
        });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, [FromQuery] string? subjectKind, [FromQuery] string? subjectId, CancellationToken ct)
    {
        if (!SubjectAccess.TryCreate(subjectKind, subjectId, out var subject))
            return BadRequest("subjectKind and subjectId name the subject the document is opened from.");

        var (document, decision) = await LoadAsync(id, subject, ct);
        await AuditAsync(DocumentAccessAction.View, id, subject, decision.Allowed, decision.Reason, ct);
        return decision.Allowed ? Ok(SubjectAccess.Project(_actor, document!)) : DeniedResult(document, subject);
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

        var (document, decision) = await LoadAsync(id, subject, ct);
        if (decision.Allowed && document!.PendingUploadPath != null)
            decision = AccessDecision.Deny("upload not finalized");
        if (decision.Allowed && !ScanAllowsServing(document!.Scan.Status))
            decision = AccessDecision.Deny($"scan status {document.Scan.Status}");

        await AuditAsync(DocumentAccessAction.Download, id, subject, decision.Allowed, decision.Reason, ct);
        if (!decision.Allowed)
            return DeniedResult(document, subject);

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

    /// <summary>
    /// A stored clinical document, read into structured content for a UM
    /// reviewer (docs/architecture/ccda.md).
    ///
    /// Takes EXACTLY the path <see cref="Content"/> takes — same subject, same
    /// access decision, same pending-upload and scan refusals, same audit
    /// action — because it serves the same bytes in a different shape. A second
    /// access path to the same content is a second place for the rules to drift.
    ///
    /// The clinical resources name the member from the document's own Member
    /// LINK, never from the C-CDA's recordTarget: matching a sender's
    /// identification of a person against CHO's is a decision about identity,
    /// not a parse. Without that link the sections, their entry counts and
    /// their narratives are still returned — a reviewer can read those — but no
    /// resource is attributed to anyone.
    /// </summary>
    [HttpGet("{id}/clinical-summary")]
    public async Task<IActionResult> ClinicalSummaryOf(
        string id, [FromQuery] string? subjectKind, [FromQuery] string? subjectId, CancellationToken ct)
    {
        if (!SubjectAccess.TryCreate(subjectKind, subjectId, out var subject))
            return BadRequest("subjectKind and subjectId name the subject the document is opened from.");

        var (document, decision) = await LoadAsync(id, subject, ct);
        if (decision.Allowed && document!.PendingUploadPath != null)
            decision = AccessDecision.Deny("upload not finalized");
        if (decision.Allowed && !ScanAllowsServing(document!.Scan.Status))
            decision = AccessDecision.Deny($"scan status {document.Scan.Status}");

        await AuditAsync(DocumentAccessAction.Download, id, subject, decision.Allowed, decision.Reason, ct);
        if (!decision.Allowed)
            return DeniedResult(document, subject);

        if (!ClinicalSummaryReader.CanRead(document!.ContentType))
        {
            // A PDF is not a failure to parse; it is a document of a kind CHO
            // reads no structure from. Saying so is more useful than an empty
            // summary that looks like a C-CDA with nothing in it.
            return UnprocessableEntity(
                $"CHO reads structured clinical content from C-CDA documents; this one is {document.ContentType}.");
        }

        Stream stream;
        try
        {
            stream = await _store.DownloadAsync(document.StorageContainer, document.StoragePath, ct);
        }
        catch (DocumentNotFoundException)
        {
            _logger.LogError("Catalog document {TenantId}/{DocumentId} has no stored content",
                LogSanitizer.Sanitize(TenantId), LogSanitizer.Sanitize(id));
            return NotFound();
        }

        using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            return Ok(_clinicalSummary.Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), MemberOf(document)));
        }
    }

    /// <summary>
    /// The member this document is filed against, as a FHIR reference, or null
    /// where it is linked to no member. Read from the catalog's own link rather
    /// than from the document's content.
    /// </summary>
    private static Hl7.Fhir.Model.ResourceReference? MemberOf(CatalogDocument document)
    {
        var member = document.Links.FirstOrDefault(l => l.Kind == DocumentLinkKind.Member);
        return member is null ? null : new Hl7.Fhir.Model.ResourceReference($"Patient/{member.Id}");
    }

    /// <summary>
    /// Who opened this document, when, from where, and whether it was allowed.
    /// Needs audit:read, and for a sensitive document documents:read-sensitive
    /// too: the log names the subjects (members) it was opened for.
    /// </summary>
    [HttpGet("{id}/access-log")]
    [RequirePermission("audit:read")]
    public async Task<IActionResult> AccessLog(string id, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var document = await _documents.GetAsync(TenantId, id, ct);
        var refusal = document is null ? "no such document in tenant"
            : document.Sensitivity.Count > 0 && !_actor.HasPermission(ChoRolePermissions.DocumentsReadSensitive)
                ? $"sensitive ({string.Join(",", document.Sensitivity)}): needs {ChoRolePermissions.DocumentsReadSensitive}"
                : null;
        await AuditAsync(DocumentAccessAction.AccessLog, id, null, allowed: refusal is null, refusal, ct);
        if (document is null)
            return NotFound();
        if (refusal != null)
            return Forbidden(RestrictedMessage);

        var entries = await _audit.ForDocumentAsync(TenantId, id, Math.Clamp(limit, 1, 1000), ct);
        return Ok(entries);
    }

    // ── Uploading ────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads a file with its metadata. Identical content already in the
    /// tenant, in a document the caller can already open and that is not on
    /// legal hold, is not stored again: that document gains this upload's
    /// links and tags (200). Otherwise the upload is a new document (201),
    /// whether or not other copies exist, so an upload reveals nothing about
    /// documents the caller can't see.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(DocumentUploadPolicy.DefaultMaxUploadBytes + 6L * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] UploadDocumentForm form, IFormFile? file, CancellationToken ct)
    {
        var metadata = await ValidateMetadataAsync(form.Type, form.Links, form.Sensitivity, form.SourceChannel, form.ReceivedAt, ct);
        if (metadata.Error is { } error)
            return error;
        var auditSubject = metadata.Links[0];
        if (ParseExternalIds(form.ExternalIds) is not { } externalIds)
            return BadRequest("ExternalIds are \"key=value\" (key: a letter then letters, digits, '-' or '_'; value: one token), at most 10.");

        if (file is null || file.Length == 0)
            return BadRequest("A file is required.");
        if (await RefuseCreationHoldAsync(form.LegalHold, form.LegalHoldReason, auditSubject, ct) is { } holdRefusal)
            return holdRefusal;
        if (file.Length > _uploadPolicy.MaxUploadBytes)
            return await RefuseUploadAsync(auditSubject, null, StatusCodes.Status413PayloadTooLarge,
                $"Documents are limited to {_uploadPolicy.MaxUploadBytes} bytes.", ct);

        var contentType = DocumentUploadPolicy.NormalizeContentType(file.ContentType);
        if (contentType is null || !_uploadPolicy.IsAllowedContentType(contentType))
            return await RefuseUploadAsync(auditSubject, null, StatusCodes.Status415UnsupportedMediaType, UnsupportedTypeMessage(file.ContentType), ct);

        await using var buffer = TempBuffer();
        await file.CopyToAsync(buffer, ct);
        if (!DocumentUploadPolicy.MatchesSignature(contentType, await PrefixAsync(buffer, ct)))
            return await RefuseUploadAsync(auditSubject, null, StatusCodes.Status415UnsupportedMediaType, $"The file content is not {contentType}.", ct);

        var sha256 = await Sha256HexAsync(buffer, ct);

        // A held upload is always its own document: merging would put someone
        // else's copy under this hold.
        if (!form.LegalHold)
        {
            var duplicate = (await _documents.FindByHashAsync(TenantId, sha256, ct)).FirstOrDefault(CanMergeInto);
            if (duplicate != null)
                return await MergeDuplicateAsync(duplicate, metadata, ct);
        }

        var id = Guid.NewGuid().ToString("N");
        var document = NewDocument(id, form.Type, form.Title, contentType, file.FileName, metadata,
            form.StateCode, form.CoverageTerminationDate, form.RetentionPolicyId);
        if (form.LegalHold)
            PlaceHold(document, form.LegalHoldReason);
        document.Source.ExternalIds = externalIds;
        document.Sha256 = sha256;
        document.SizeBytes = buffer.Length;

        buffer.Position = 0;
        document.Scan = await _scanner.ScanAsync(buffer, contentType, ct);
        buffer.Position = 0;

        // Audited before anything is written: a failed audit stores nothing.
        await AuditAsync(DocumentAccessAction.Upload, id, auditSubject, allowed: true,
            $"{document.Type}, links [{string.Join(",", document.Links)}]{HoldNote(document)}", ct);

        await _store.UploadAsync(Container, document.StoragePath, buffer, contentType, StorageTags(document), ct);
        try
        {
            await _documents.CreateAsync(document, ct);
        }
        catch
        {
            // No record means nothing can reach the bytes; don't leave them behind.
            await _store.DeleteAsync(Container, document.StoragePath, CancellationToken.None);
            throw;
        }

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
        var metadata = await ValidateMetadataAsync(request.Type, request.Links, request.Sensitivity, request.SourceChannel, request.ReceivedAt, ct);
        if (metadata.Error is { } error)
            return error;
        var auditSubject = metadata.Links[0];
        if (await RefuseCreationHoldAsync(request.LegalHold, request.LegalHoldReason, auditSubject, ct) is { } holdRefusal)
            return holdRefusal;

        var contentType = DocumentUploadPolicy.NormalizeContentType(request.ContentType);
        if (contentType is null || !_uploadPolicy.IsAllowedContentType(contentType))
            return await RefuseUploadAsync(auditSubject, null, StatusCodes.Status415UnsupportedMediaType, UnsupportedTypeMessage(request.ContentType), ct);

        var id = Guid.NewGuid().ToString("N");
        var document = NewDocument(id, request.Type, request.Title, contentType, request.FileName, metadata,
            request.StateCode, request.CoverageTerminationDate, request.RetentionPolicyId);
        document.PendingUploadPath = $"{document.StoragePath}.upload";
        if (request.LegalHold)
            PlaceHold(document, request.LegalHoldReason);

        var expires = _clock.GetUtcNow().Add(_options.UploadLinkLifetime);
        var link = await _store.CreateUploadLinkAsync(Container, document.PendingUploadPath, contentType, expires, ct);
        if (link is null)
        {
            return await RefuseUploadAsync(auditSubject, null, StatusCodes.Status501NotImplemented,
                "Pre-signed uploads need storage credentials that can sign upload links; upload the file with multipart/form-data instead.", ct);
        }

        await AuditAsync(DocumentAccessAction.Upload, id, auditSubject, allowed: true,
            $"pre-signed upload started: {document.Type}, links [{string.Join(",", document.Links)}]{HoldNote(document)}", ct);
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
    /// Finishes a pre-signed upload. Only the uploader may finalize: the same
    /// caller, and for a service the same user it acted for. The staged
    /// object is size-checked before it is read, then checked against the
    /// declared type, hashed and scanned, and copied to the document's path,
    /// which no upload link covers. A duplicate the caller may merge into is
    /// merged; the staged upload is kept until that succeeds.
    /// </summary>
    [HttpPost("{id}/finalize")]
    public async Task<IActionResult> Finalize(string id, CancellationToken ct)
    {
        var document = await _documents.GetAsync(TenantId, id, ct);
        if (document is null || document.PendingUploadPath is null
            || document.CreatedBy != _actor.UserId || document.CreatedOnBehalfOf != OnBehalfOf)
        {
            await AuditAsync(DocumentAccessAction.Upload, id, null, allowed: false,
                document is null ? "finalize: no such document" : "finalize: not a pending upload by this caller", ct);
            return NotFound();
        }

        var auditSubject = document.Links[0];
        var staging = document.PendingUploadPath;

        DocumentProperties staged;
        try
        {
            staged = await _store.GetPropertiesAsync(Container, staging, ct);
        }
        catch (DocumentNotFoundException)
        {
            return await RefuseUploadAsync(auditSubject, id, StatusCodes.Status409Conflict,
                "Nothing has been uploaded for this document yet.", ct);
        }
        if (staged.SizeBytes > _uploadPolicy.MaxUploadBytes)
        {
            await _store.DeleteAsync(Container, staging, ct);
            return await RefuseUploadAsync(auditSubject, id, StatusCodes.Status413PayloadTooLarge,
                $"Documents are limited to {_uploadPolicy.MaxUploadBytes} bytes; the upload was discarded.", ct);
        }

        await using var buffer = TempBuffer();
        await using (var content = await _store.DownloadAsync(Container, staging, ct))
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > _uploadPolicy.MaxUploadBytes)
                {
                    await _store.DeleteAsync(Container, staging, ct);
                    return await RefuseUploadAsync(auditSubject, id, StatusCodes.Status413PayloadTooLarge,
                        $"Documents are limited to {_uploadPolicy.MaxUploadBytes} bytes; the upload was discarded.", ct);
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            }
        }

        if (buffer.Length == 0
            || !DocumentUploadPolicy.MatchesSignature(document.ContentType, await PrefixAsync(buffer, ct)))
        {
            await _store.DeleteAsync(Container, staging, ct);
            return await RefuseUploadAsync(auditSubject, id, StatusCodes.Status415UnsupportedMediaType,
                $"The uploaded content is not {document.ContentType}; the upload was discarded.", ct);
        }

        var sha256 = await Sha256HexAsync(buffer, ct);
        var duplicate = document.Retention.LegalHold.Set
            ? null
            : (await _documents.FindByHashAsync(TenantId, sha256, ct)).FirstOrDefault(CanMergeInto);
        if (duplicate != null)
        {
            var merged = await MergeDuplicateAsync(duplicate,
                new UploadMetadata(document.Links, document.Sensitivity, document.Source.Channel, document.ReceivedAt, null), ct);
            if (merged is OkObjectResult)
            {
                await _store.DeleteAsync(Container, staging, ct);
                await _documents.DeletePendingAsync(TenantId, document.Id, ct);
            }
            return merged;
        }

        buffer.Position = 0;
        document.Scan = await _scanner.ScanAsync(buffer, document.ContentType, ct);
        buffer.Position = 0;
        document.Sha256 = sha256;
        document.SizeBytes = buffer.Length;
        document.PendingUploadPath = null;
        // Each finalize attempt writes its own object, so a concurrent finalize
        // of the same upload (a retry, a double click) can never overwrite or
        // delete the content the winning attempt recorded.
        document.StoragePath = DocumentUploadPolicy.BuildStoragePath(
            TenantId, $"{document.Id}-{Guid.NewGuid():N}"[..(document.Id.Length + 9)], document.ContentType, document.StoragePath);

        await AuditAsync(DocumentAccessAction.Upload, id, auditSubject, allowed: true,
            $"pre-signed upload finalized: {document.Type}", ct);
        await _store.UploadAsync(Container, document.StoragePath, buffer, document.ContentType, StorageTags(document), ct);

        try
        {
            document = await _documents.UpdateAsync(document, ct);
        }
        catch (DocumentConcurrencyException)
        {
            // Another attempt won; this attempt's own object is unreferenced.
            await _store.DeleteAsync(Container, document.StoragePath, CancellationToken.None);
            return ConcurrencyConflict();
        }

        await _store.DeleteAsync(Container, staging, ct);
        return Ok(SubjectAccess.Project(_actor, document));
    }

    // ── Links ────────────────────────────────────────────────────────────

    /// <summary>Links the document to another subject. The caller works from a subject it is already linked to, and must be able to read the new one.</summary>
    [HttpPost("{id}/links")]
    public async Task<IActionResult> AddLink(string id, [FromQuery] string? subjectKind, [FromQuery] string? subjectId,
        [FromBody] LinkRequest request, CancellationToken ct)
    {
        if (!SubjectAccess.TryCreate(subjectKind, subjectId, out var subject))
            return BadRequest("subjectKind and subjectId name the subject the document is opened from.");
        if (!SubjectAccess.TryParseLink(request.Link, out var link))
            return BadRequest("Link is \"Kind:Id\", e.g. \"Appeal:A-123\".");

        var (document, decision) = await LoadAsync(id, subject, ct);
        IActionResult? refusal = null;
        if (!decision.Allowed)
            refusal = DeniedResult(document, subject);
        else if (!SubjectAccess.CanReadSubject(_actor, link.Kind))
            (decision, refusal) = (AccessDecision.Deny(SubjectPermissionMessage(link.Kind)), Forbidden(SubjectPermissionMessage(link.Kind)));
        else if (document!.Retention.LegalHold.Set)
            (decision, refusal) = (AccessDecision.Deny("legal hold"), LegalHoldConflict());

        await AuditAsync(DocumentAccessAction.Link, id, subject, decision.Allowed,
            decision.Allowed ? $"add {link}" : $"add {link}: {decision.Reason}", ct);
        if (refusal != null)
            return refusal;

        if (document!.Links.Contains(link))
            return Ok(SubjectAccess.Project(_actor, document));

        document.Links.Add(link);
        return await SaveAsync(document, ct);
    }

    /// <summary>Removes one link. A document always keeps at least one, and a held document keeps all of them.</summary>
    [HttpDelete("{id}/links/{kind}/{linkId}")]
    public async Task<IActionResult> RemoveLink(string id, string kind, string linkId,
        [FromQuery] string? subjectKind, [FromQuery] string? subjectId, CancellationToken ct)
    {
        if (!SubjectAccess.TryCreate(subjectKind, subjectId, out var subject))
            return BadRequest("subjectKind and subjectId name the subject the document is opened from.");
        if (!SubjectAccess.TryCreate(kind, linkId, out var link))
            return BadRequest("Unknown link.");

        var (document, decision) = await LoadAsync(id, subject, ct);
        IActionResult? refusal = null;
        if (!decision.Allowed)
            refusal = DeniedResult(document, subject);
        else if (!SubjectAccess.CanReadSubject(_actor, link.Kind))
            (decision, refusal) = (AccessDecision.Deny(SubjectPermissionMessage(link.Kind)), Forbidden(SubjectPermissionMessage(link.Kind)));
        else if (document!.Retention.LegalHold.Set)
            (decision, refusal) = (AccessDecision.Deny("legal hold"), LegalHoldConflict());
        else if (!document.Links.Contains(link))
            (decision, refusal) = (AccessDecision.Deny("no such link"), NotFound());
        else if (document.Links.Count == 1)
            (decision, refusal) = (AccessDecision.Deny("last link"), Conflict("A document keeps at least one link. Link it elsewhere first."));

        await AuditAsync(DocumentAccessAction.Unlink, id, subject, decision.Allowed,
            decision.Allowed ? $"remove {link}" : $"remove {link}: {decision.Reason}", ct);
        if (refusal != null)
            return refusal;

        document!.Links.Remove(link);
        return await SaveAsync(document, ct);
    }

    // ── Legal hold ───────────────────────────────────────────────────────

    /// <summary>
    /// Places or releases a legal hold (records:legal-hold). A held document's
    /// links can't change; deletion (the retention job, a later step) skips
    /// it. A release needs a reason. Every attempt is audited. The answer is
    /// only the hold state: placing a hold does not open the document.
    /// </summary>
    [HttpPut("{id}/legal-hold")]
    [RequirePermission(ChoRolePermissions.LegalHold)]
    public async Task<IActionResult> UpdateLegalHold(string id, [FromBody] LegalHoldRequest request, CancellationToken ct)
    {
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        var verb = request.LegalHold ? "set" : "release";

        if (!request.LegalHold && reason is null)
        {
            await AuditAsync(DocumentAccessAction.LegalHold, id, null, allowed: false, $"{verb}: no reason given", ct);
            return BadRequest("A reason is required to release a legal hold.");
        }
        if (reason?.Length > LegalHoldRequest.MaxReasonLength)
        {
            await AuditAsync(DocumentAccessAction.LegalHold, id, null, allowed: false, $"{verb}: reason too long", ct);
            return BadRequest($"A legal hold reason is at most {LegalHoldRequest.MaxReasonLength} characters.");
        }

        var document = await _documents.GetAsync(TenantId, id, ct);
        if (document is null)
        {
            await AuditAsync(DocumentAccessAction.LegalHold, id, null, allowed: false, $"{verb}: no such document", ct);
            return NotFound();
        }

        var hold = document.Retention.LegalHold;
        if (hold.Set == request.LegalHold)
        {
            await AuditAsync(DocumentAccessAction.LegalHold, id, null, allowed: true, $"{verb}: already in that state", ct);
            return Ok(HoldState(document));
        }

        if (request.LegalHold)
            PlaceHold(document, reason);
        else
            ReleaseHold(hold, reason!);

        await AuditAsync(DocumentAccessAction.LegalHold, id, null, allowed: true, $"{verb}: {reason ?? "(no reason)"}", ct);
        try
        {
            document = await _documents.UpdateAsync(document, ct);
        }
        catch (DocumentConcurrencyException)
        {
            return ConcurrencyConflict();
        }

        if (document.PendingUploadPath is null)
        {
            try
            {
                await _store.SetTagsAsync(document.StorageContainer, document.StoragePath, StorageTags(document), ct);
            }
            catch (Exception ex)
            {
                // The catalog is the record of the hold and is already saved;
                // storage tags back it up for lifecycle rules. Say so loudly.
                _logger.LogError(ex, "Legal hold on {TenantId}/{DocumentId} saved, but its storage tags could not be updated",
                    LogSanitizer.Sanitize(TenantId), LogSanitizer.Sanitize(id));
            }
        }

        return Ok(HoldState(document));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private sealed record UploadMetadata(
        List<DocumentLink> Links, List<string> Sensitivity, string Channel, DateTime ReceivedAt, IActionResult? Error);

    private async Task<UploadMetadata> ValidateMetadataAsync(
        string? type, List<string>? links, List<string>? sensitivity, string? channel, DateTime? receivedAt, CancellationToken ct)
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
            {
                var reason = SubjectPermissionMessage(link.Kind);
                await AuditAsync(DocumentAccessAction.Upload, null, link, allowed: false, reason, ct);
                return Fail(Forbidden(reason));
            }
        }

        var tags = (sensitivity ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();
        if (tags.FirstOrDefault(t => !SensitivityTags.All.Contains(t)) is { } unknown)
            return Fail(BadRequest($"Unknown sensitivity tag '{LogSanitizer.Sanitize(unknown)}'."));

        var sourceChannel = string.IsNullOrWhiteSpace(channel) ? DocumentChannels.Upload : channel.Trim();
        if (!DocumentChannels.All.Contains(sourceChannel))
            return Fail(BadRequest($"Unknown source channel '{LogSanitizer.Sanitize(sourceChannel)}'."));

        var now = Now;
        var received = receivedAt?.ToUniversalTime() ?? now;
        if (received > now.AddMinutes(5) || received < now.AddYears(-30))
            return Fail(BadRequest("ReceivedAt is when the document arrived: not in the future, and within the last 30 years."));

        return new UploadMetadata(parsed, tags, sourceChannel, received, null);
    }

    private CatalogDocument NewDocument(string id, string type, string? title, string contentType, string? fileName,
        UploadMetadata metadata, string? stateCode, DateTime? coverageTerminationDate, string? retentionPolicyId)
    {
        if (!DocumentUploadPolicy.IsSafePathSegment(TenantId))
            throw new InvalidOperationException("Tenant id cannot be used in a storage path.");

        var policy = _retention.ResolvePolicy(stateCode, coverageTerminationDate, retentionPolicyId);
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
            CreatedOnBehalfOf = OnBehalfOf,
            CreatedAt = Now,
            ReceivedAt = metadata.ReceivedAt
        };
    }

    /// <summary>
    /// A duplicate is merged only into a document this caller can already open
    /// (through one of its links, sensitivity included) and that is not on
    /// legal hold. Anything else becomes its own document, so identical bytes
    /// never attach anyone to a record they couldn't see, and the answer looks
    /// the same as for a first upload.
    /// </summary>
    private bool CanMergeInto(CatalogDocument existing) =>
        !existing.Retention.LegalHold.Set
        && existing.Links.Any(l => SubjectAccess.Decide(_actor, existing, l).Allowed);

    private async Task<IActionResult> MergeDuplicateAsync(CatalogDocument existing, UploadMetadata metadata, CancellationToken ct)
    {
        var added = metadata.Links.Where(l => !existing.Links.Contains(l)).ToList();
        foreach (var link in added)
            existing.Links.Add(link);
        foreach (var tag in metadata.Sensitivity.Where(t => !existing.Sensitivity.Contains(t)))
            existing.Sensitivity.Add(tag);

        await AuditAsync(DocumentAccessAction.Upload, existing.Id, metadata.Links[0], allowed: true,
            $"duplicate content: linked the existing document to [{string.Join(",", added)}]", ct);
        try
        {
            existing = await _documents.UpdateAsync(existing, ct);
        }
        catch (DocumentConcurrencyException)
        {
            return ConcurrencyConflict();
        }

        // Adding a sensitivity tag can make the merged document restricted for
        // this very caller; then they get only its id.
        return SubjectAccess.DecideSensitivity(_actor, existing).Allowed
            ? Ok(SubjectAccess.Project(_actor, existing))
            : Ok(new { id = existing.Id });
    }

    /// <summary>The document and whether this caller may open it from <paramref name="subject"/>. Does not audit.</summary>
    private async Task<(CatalogDocument? Document, AccessDecision Decision)> LoadAsync(string id, DocumentLink subject, CancellationToken ct)
    {
        var document = await _documents.GetAsync(TenantId, id, ct);
        var decision = document is null
            ? AccessDecision.Deny("no such document in tenant")
            : SubjectAccess.Decide(_actor, document, subject);
        return (document, decision);
    }

    /// <summary>
    /// 403 naming the permission when the caller lacks a subject permission it
    /// could be granted; 404 when the document is missing or not on that
    /// subject; otherwise (sensitivity) a 403 that says nothing more than
    /// "restricted". The reason itself is only in the audit.
    /// </summary>
    private IActionResult DeniedResult(CatalogDocument? document, DocumentLink subject)
    {
        if (!SubjectAccess.CanReadSubject(_actor, subject.Kind))
            return Forbidden(SubjectPermissionMessage(subject.Kind));
        if (document is null || !document.Links.Contains(subject))
            return NotFound();
        if (!SubjectAccess.DecideSensitivity(_actor, document).Allowed)
            return Forbidden(RestrictedMessage);
        if (document.PendingUploadPath != null)
            return Conflict("The upload for this document has not been finalized.");
        return Conflict($"The document can't be opened: scan status {document.Scan.Status}.");
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

    private async Task<IActionResult> RefuseUploadAsync(DocumentLink subject, string? documentId, int status, string message, CancellationToken ct)
    {
        await AuditAsync(DocumentAccessAction.Upload, documentId, subject, allowed: false, message, ct);
        return StatusCode(status, message);
    }

    /// <summary>Written before the response; a failure here fails the request, so nothing is served or changed unaudited.</summary>
    private Task AuditAsync(DocumentAccessAction action, string? documentId, DocumentLink? subject, bool allowed, string? detail, CancellationToken ct)
    {
        var screen = Request.Headers[ScreenHeader].ToString();
        return _audit.RecordAsync(new DocumentAccessEvent
        {
            TenantId = TenantId,
            DocumentId = documentId,
            Actor = _actor.UserId,
            OnBehalfOf = OnBehalfOf,
            At = Now,
            Action = action,
            SubjectKind = subject?.Kind.ToString(),
            SubjectId = subject?.Id,
            Screen = string.IsNullOrWhiteSpace(screen) ? null : LogSanitizer.Sanitize(screen.Length > 64 ? screen[..64] : screen),
            Allowed = allowed,
            Detail = detail
        }, ct);
    }

    private static Dictionary<string, string>? ParseExternalIds(List<string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) return null;
            var key = entry[..eq].Trim();
            var value = entry[(eq + 1)..].Trim();
            if (!ExternalIdKey.IsMatch(key) || !SafeToken.IsMatch(value)) return null;
            result[key] = value;
        }
        return result.Count <= 10 ? result : null;
    }

    /// <summary>
    /// A hold asked for at upload: refused (403, audited) without
    /// records:legal-hold, or (400) with an over-long reason. Null when fine.
    /// </summary>
    private async Task<IActionResult?> RefuseCreationHoldAsync(bool legalHold, string? reason, DocumentLink subject, CancellationToken ct)
    {
        if (!legalHold)
            return null;
        if (!_actor.HasPermission(ChoRolePermissions.LegalHold))
            return await RefuseUploadAsync(subject, null, StatusCodes.Status403Forbidden,
                $"Placing a legal hold requires {ChoRolePermissions.LegalHold}.", ct);
        if (reason?.Trim().Length > LegalHoldRequest.MaxReasonLength)
            return BadRequest($"A legal hold reason is at most {LegalHoldRequest.MaxReasonLength} characters.");
        return null;
    }

    private void PlaceHold(CatalogDocument document, string? reason)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var now = Now;
        var hold = document.Retention.LegalHold;
        hold.Set = true;
        hold.By = ActingUser;
        hold.At = now;
        hold.Reason = reason;
        hold.History.Add(new LegalHoldEvent { Action = LegalHoldAction.Set, Actor = ActingUser, Reason = reason, At = now });
    }

    private void ReleaseHold(LegalHold hold, string reason)
    {
        hold.Set = false;
        hold.By = null;
        hold.At = null;
        hold.Reason = null;
        hold.History.Add(new LegalHoldEvent { Action = LegalHoldAction.Released, Actor = ActingUser, Reason = reason, At = Now });
    }

    private static string HoldNote(CatalogDocument d) =>
        d.Retention.LegalHold.Set ? $", under legal hold: {d.Retention.LegalHold.Reason ?? "(no reason)"}" : string.Empty;

    private static object HoldState(CatalogDocument d) => new { id = d.Id, legalHold = d.Retention.LegalHold.Set };

    private static IReadOnlyDictionary<string, string> StorageTags(CatalogDocument d) => new Dictionary<string, string>
    {
        ["retentionPolicyId"] = d.Retention.PolicyId,
        ["retentionUntilDate"] = d.Retention.RetainUntil.ToString("yyyy-MM-dd"),
        ["legalHold"] = d.Retention.LegalHold.Set ? "true" : "false"
    };

    /// <summary>
    /// Uploads are staged on local disk (deleted on close) rather than in
    /// memory: documents run to tens of megabytes and several can arrive at once.
    /// </summary>
    private static FileStream TempBuffer() => new(
        Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None,
        bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);

    /// <summary>The first bytes of the staged content (for the signature check); leaves the stream at the start.</summary>
    private static async Task<byte[]> PrefixAsync(Stream buffer, CancellationToken ct)
    {
        buffer.Position = 0;
        var prefix = new byte[16];
        var total = 0;
        int read;
        while (total < prefix.Length && (read = await buffer.ReadAsync(prefix.AsMemory(total), ct)) > 0)
            total += read;
        buffer.Position = 0;
        return prefix[..total];
    }

    private static async Task<string> Sha256HexAsync(Stream buffer, CancellationToken ct)
    {
        buffer.Position = 0;
        var hash = await SHA256.HashDataAsync(buffer, ct);
        buffer.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SubjectPermissionMessage(DocumentLinkKind kind) =>
        $"needs {SubjectAccess.ReadPermissionFor(kind)} for {kind} documents";

    private ObjectResult Forbidden(string reason) => StatusCode(StatusCodes.Status403Forbidden, reason);

    private string UnsupportedTypeMessage(string? given) =>
        $"Content type '{LogSanitizer.Sanitize(given)}' is not accepted. Allowed: {string.Join(", ", _uploadPolicy.AllowedContentTypes)}.";

    private ObjectResult ConcurrencyConflict() => StatusCode(StatusCodes.Status409Conflict,
        "The document was changed by another request. Reload it and try again.");

    private ObjectResult LegalHoldConflict() => StatusCode(StatusCodes.Status409Conflict,
        "The document is under legal hold; its links can't change until the hold is released.");
}
