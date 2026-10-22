using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using CorrespondenceService.Models;
using CorrespondenceService.Repositories;
using CorrespondenceService.Services;
using Microsoft.AspNetCore.Mvc;

namespace CorrespondenceService.Controllers;

/// <summary>
/// Letter types, templates and letters (docs/architecture/correspondence.md,
/// build step 1). Reading needs <c>correspondence:read</c>; requesting a
/// letter needs <c>correspondence:write</c> plus, enforced by the document
/// catalog, documents:write and the read permission of every subject;
/// creating letter types and template versions needs
/// <c>correspondence:templates</c>; approving or retiring a template version
/// needs <c>correspondence:approve</c>.
/// </summary>
[ApiController]
[Route("api/v1/correspondence")]
public sealed class CorrespondenceController : ControllerBase
{
    public const string Read = ChoRolePermissions.CorrespondenceRead;
    public const string Write = ChoRolePermissions.CorrespondenceWrite;
    public const string Templates = ChoRolePermissions.CorrespondenceTemplates;
    public const string Approve = ChoRolePermissions.CorrespondenceApprove;

    private readonly ICorrespondenceRepository _repository;
    private readonly LetterComposer _composer;
    private readonly ILetterDocumentStore _documents;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _clock;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CorrespondenceController> _logger;

    public CorrespondenceController(
        ICorrespondenceRepository repository,
        LetterComposer composer,
        ILetterDocumentStore documents,
        ICurrentActor actor,
        TimeProvider clock,
        IConfiguration configuration,
        ILogger<CorrespondenceController> logger)
    {
        _repository = repository;
        _composer = composer;
        _documents = documents;
        _actor = actor;
        _clock = clock;
        _configuration = configuration;
        _logger = logger;
    }

    private string TenantId => _actor.TenantId;

    /// <summary>On by default: the author of a template version cannot approve it.</summary>
    private bool RequireSeparateApprover => _configuration.GetValue("Correspondence:RequireSeparateApprover", true);

    // ── Letter types ─────────────────────────────────────────────────

    [HttpGet("letter-types")]
    [RequirePermission(Read)]
    public async Task<IActionResult> ListLetterTypes(CancellationToken ct) =>
        Ok(await _repository.ListLetterTypesAsync(TenantId, ct));

    [HttpPost("letter-types")]
    [RequirePermission(Templates)]
    public async Task<IActionResult> CreateLetterType([FromBody] CreateLetterTypeRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z][A-Z0-9_]{1,63}$"))
            return Problem400("Code must be upper-case letters, digits and '_' (2-64), starting with a letter.");
        if (string.IsNullOrWhiteSpace(request.Name))
            return Problem400("Name is required.");

        var letterType = new LetterType
        {
            Id = Guid.NewGuid().ToString("N"),
            TenantId = TenantId,
            Code = code,
            Name = request.Name.Trim(),
            Category = request.Category,
            Audience = request.Audience,
            RequiresApproval = request.RequiresApproval,
            DocumentType = string.IsNullOrWhiteSpace(request.DocumentType) ? "OutboundLetter" : request.DocumentType.Trim(),
            IsSignificantCommunication = request.IsSignificantCommunication,
            IsRequiredMaterial = request.IsRequiredMaterial,
            CreatedAt = Now,
            CreatedBy = _actor.UserId,
        };
        if (!await _repository.TryCreateLetterTypeAsync(letterType, ct))
            return Conflict(new { error = $"Letter type {code} already exists." });
        return CreatedAtAction(nameof(ListLetterTypes), null, letterType);
    }

    // ── Templates ────────────────────────────────────────────────────

    [HttpGet("templates")]
    [RequirePermission(Read)]
    public async Task<IActionResult> ListTemplates([FromQuery] string? letterType, CancellationToken ct) =>
        Ok(await _repository.ListTemplatesAsync(TenantId, letterType, ct));

    [HttpGet("templates/{templateId}/versions")]
    [RequirePermission(Read)]
    public async Task<IActionResult> ListVersions(string templateId, CancellationToken ct)
    {
        var versions = await _repository.ListTemplateVersionsAsync(TenantId, templateId, ct);
        return versions.Count == 0 ? NotFound() : Ok(versions);
    }

    [HttpGet("templates/{templateId}/versions/{version:int}")]
    [RequirePermission(Read)]
    public async Task<IActionResult> GetVersion(string templateId, int version, CancellationToken ct)
    {
        var template = await _repository.GetTemplateVersionAsync(TenantId, templateId, version, ct);
        return template is null ? NotFound() : Ok(template);
    }

    /// <summary>Creates a new template: version 1, as a draft.</summary>
    [HttpPost("templates")]
    [RequirePermission(Templates)]
    public async Task<IActionResult> CreateTemplate([FromBody] TemplateContentRequest request, CancellationToken ct)
    {
        var template = FromRequest(request, "TPL-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(), 1);
        var errors = TemplateRules.ValidateDraft(template, await LetterTypeCodesAsync(ct), previousApproved: null);
        if (errors.Count > 0) return Invalid(errors);
        if (!await _repository.TryCreateTemplateVersionAsync(template, ct))
            return Conflict(new { error = "Template already exists." });
        return CreatedAtAction(nameof(GetVersion), new { templateId = template.TemplateId, version = 1 }, template);
    }

    /// <summary>Starts the next version of a template as a draft. Locked text of a Standardized or State-prescribed template carries over unchanged.</summary>
    [HttpPost("templates/{templateId}/versions")]
    [RequirePermission(Templates)]
    public async Task<IActionResult> CreateVersion(string templateId, [FromBody] TemplateContentRequest request, CancellationToken ct)
    {
        var versions = await _repository.ListTemplateVersionsAsync(TenantId, templateId, ct);
        if (versions.Count == 0) return NotFound();
        if (versions.Any(v => v.Status == TemplateStatus.Draft))
            return Conflict(new { error = "This template already has a draft version; edit or approve it first." });

        var latest = versions[^1];
        if (request.LetterTypeCode != latest.LetterTypeCode)
            return Problem400("A new version keeps the template's letter type.");
        if (request.Kind != latest.Kind)
            return Problem400("A new version keeps the template's kind.");

        var template = FromRequest(request, templateId, latest.Version + 1);
        var previousApproved = versions.Where(v => v.Status != TemplateStatus.Draft && v.ApprovedAt is not null).MaxBy(v => v.Version);
        var errors = TemplateRules.ValidateDraft(template, await LetterTypeCodesAsync(ct), previousApproved);
        if (errors.Count > 0) return Invalid(errors);
        if (!await _repository.TryCreateTemplateVersionAsync(template, ct))
            return Conflict(new { error = "That version was just created by someone else; reload." });
        return CreatedAtAction(nameof(GetVersion), new { templateId, version = template.Version }, template);
    }

    /// <summary>Edits a draft version. Approved and retired versions never change.</summary>
    [HttpPut("templates/{templateId}/versions/{version:int}")]
    [RequirePermission(Templates)]
    public async Task<IActionResult> EditDraft(string templateId, int version, [FromBody] TemplateContentRequest request, CancellationToken ct)
    {
        var existing = await _repository.GetTemplateVersionAsync(TenantId, templateId, version, ct);
        if (existing is null) return NotFound();
        if (existing.Status != TemplateStatus.Draft)
            return Conflict(new { error = $"Version {version} is {existing.Status}; only a draft can change. Create a new version." });
        if (request.LetterTypeCode != existing.LetterTypeCode || request.Kind != existing.Kind)
            return Problem400("A draft keeps its letter type and kind.");

        var updated = FromRequest(request, templateId, version);
        updated.CreatedAt = existing.CreatedAt;
        updated.CreatedBy = existing.CreatedBy;
        updated.Revision = existing.Revision;
        var previousApproved = (await _repository.ListTemplateVersionsAsync(TenantId, templateId, ct))
            .Where(v => v.Version < version && v.ApprovedAt is not null).MaxBy(v => v.Version);
        var errors = TemplateRules.ValidateDraft(updated, await LetterTypeCodesAsync(ct), previousApproved);
        if (errors.Count > 0) return Invalid(errors);
        try
        {
            await _repository.SaveTemplateVersionAsync(updated, existing.Revision, ct);
        }
        catch (CorrespondenceConcurrencyException)
        {
            return Conflict(new { error = "The draft changed since it was read; reload and try again." });
        }
        return Ok(updated);
    }

    /// <summary>Approves a draft: its locked text is frozen and it can be used for letters from its effective date.</summary>
    [HttpPost("templates/{templateId}/versions/{version:int}/approve")]
    [RequirePermission(Approve)]
    public async Task<IActionResult> ApproveVersion(string templateId, int version, CancellationToken ct)
    {
        var template = await _repository.GetTemplateVersionAsync(TenantId, templateId, version, ct);
        if (template is null) return NotFound();
        var errors = TemplateRules.ValidateApproval(template, _actor.UserId, RequireSeparateApprover);
        var previousApproved = (await _repository.ListTemplateVersionsAsync(TenantId, templateId, ct))
            .Where(v => v.Version < version && v.ApprovedAt is not null).MaxBy(v => v.Version);
        errors.AddRange(TemplateRules.ValidateDraft(template, await LetterTypeCodesAsync(ct), previousApproved));
        if (errors.Count > 0) return Invalid(errors);

        var revision = template.Revision;
        TemplateRules.StampApproval(template, _actor.UserId, Now);
        try
        {
            await _repository.SaveTemplateVersionAsync(template, revision, ct);
        }
        catch (CorrespondenceConcurrencyException)
        {
            return Conflict(new { error = "The draft changed since it was read; reload and try again." });
        }
        _logger.LogInformation("Template {TemplateId} v{Version} approved in tenant {TenantId}",
            LogSanitizer.Sanitize(templateId), version, LogSanitizer.Sanitize(TenantId));
        return Ok(template);
    }

    /// <summary>Retires an approved version: no new letter uses it; letters already built from it keep pointing at it.</summary>
    [HttpPost("templates/{templateId}/versions/{version:int}/retire")]
    [RequirePermission(Approve)]
    public async Task<IActionResult> RetireVersion(string templateId, int version, [FromBody] RetireRequest? request, CancellationToken ct)
    {
        var template = await _repository.GetTemplateVersionAsync(TenantId, templateId, version, ct);
        if (template is null) return NotFound();
        if (template.Status != TemplateStatus.Approved)
            return Conflict(new { error = $"Only an approved version can be retired; this one is {template.Status}." });
        var revision = template.Revision;
        template.Status = TemplateStatus.Retired;
        template.RetiredBy = _actor.UserId;
        template.RetiredAt = Now;
        try
        {
            await _repository.SaveTemplateVersionAsync(template, revision, ct);
        }
        catch (CorrespondenceConcurrencyException)
        {
            return Conflict(new { error = "The version changed since it was read; reload and try again." });
        }
        return Ok(template);
    }

    /// <summary>Renders a version against sample data and returns the PDF; the pre-release checks are in the X-CHO-Letter-Checks header. Nothing is stored.</summary>
    [HttpPost("templates/{templateId}/versions/{version:int}/preview")]
    [RequirePermission(Templates)]
    public async Task<IActionResult> Preview(string templateId, int version, [FromBody] PreviewRequest request, CancellationToken ct)
    {
        var template = await _repository.GetTemplateVersionAsync(TenantId, templateId, version, ct);
        if (template is null) return NotFound();
        var result = await _composer.PreviewAsync(TenantId, template, request, ct);
        Response.Headers["X-CHO-Letter-Checks"] = string.Join("; ", result.Checks.Select(c => $"{c.Name}={(c.Passed ? "pass" : "fail")}"));
        return File(result.Pdf, "application/pdf", $"{templateId}-v{version}-preview.pdf");
    }

    /// <summary>The checks of a preview as JSON, for template authors.</summary>
    [HttpPost("templates/{templateId}/versions/{version:int}/preview/checks")]
    [RequirePermission(Templates)]
    public async Task<IActionResult> PreviewChecks(string templateId, int version, [FromBody] PreviewRequest request, CancellationToken ct)
    {
        var template = await _repository.GetTemplateVersionAsync(TenantId, templateId, version, ct);
        if (template is null) return NotFound();
        var result = await _composer.PreviewAsync(TenantId, template, request, ct);
        return Ok(new { passed = result.Passed, pageCount = result.PageCount, checks = result.Checks });
    }

    // ── Letters ──────────────────────────────────────────────────────

    /// <summary>
    /// Requests one letter: resolves the template, renders, checks and stores
    /// it. 201 with the letter; 422 with the letter and its failed checks when
    /// a check fails (nothing is stored); 422 when no template applies.
    /// </summary>
    [HttpPost("letters")]
    [RequirePermission(Write)]
    public async Task<IActionResult> RequestLetter([FromBody] LetterRequest request, CancellationToken ct)
    {
        var unreadable = request.Subjects.Where(s => LetterAccess.IsKnownSubjectKind(s.Kind ?? "") && !LetterAccess.CanReadSubject(_actor, s.Kind!)).ToList();
        if (unreadable.Count > 0)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "You need read access to every subject of the letter: " +
                        string.Join(", ", unreadable.Select(s => $"{s.Kind} ({LetterAccess.SubjectReadPermission[s.Kind]})")),
            });

        var result = await _composer.ComposeAsync(TenantId, _actor.UserId, request, ct);
        return result.Outcome switch
        {
            ComposeOutcome.Rendered => CreatedAtAction(nameof(GetLetter), new { letterId = result.Letter!.Id }, result.Letter),
            ComposeOutcome.CheckFailed => UnprocessableEntity(new { error = "The letter failed its pre-release checks; nothing was stored.", letter = result.Letter, failures = result.Errors }),
            ComposeOutcome.NoTemplate => UnprocessableEntity(new { error = result.Errors[0] }),
            ComposeOutcome.StorageFailed => StatusCode(StatusCodes.Status502BadGateway, new { error = result.Errors[0] }),
            _ => Invalid(result.Errors),
        };
    }

    [HttpGet("letters/{letterId}")]
    [RequirePermission(Read)]
    public async Task<IActionResult> GetLetter(string letterId, CancellationToken ct)
    {
        var letter = await _repository.GetLetterAsync(TenantId, letterId, ct);
        if (letter is null || !LetterAccess.CanRead(_actor, letter)) return NotFound();
        return Ok(letter);
    }

    /// <summary>
    /// The tenant's newest letters, newest first. Only letters with a subject
    /// the caller can read are returned, so the list can come up short of
    /// <paramref name="limit"/>.
    /// </summary>
    [HttpGet("letters/recent")]
    [RequirePermission(Read)]
    public async Task<IActionResult> ListRecentLetters([FromQuery] LetterStatus? status, [FromQuery] string? letterTypeCode,
        [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var letters = await _repository.ListRecentLettersAsync(TenantId, status, letterTypeCode?.Trim().ToUpperInvariant(), limit, ct);
        return Ok(letters.Where(l => LetterAccess.CanRead(_actor, l)).ToList());
    }

    /// <summary>Counts of the tenant's letters: today (UTC) and the last seven days, by outcome. No PHI.</summary>
    [HttpGet("letters/summary")]
    [RequirePermission(Read)]
    public async Task<IActionResult> LetterSummary(CancellationToken ct)
    {
        var now = Now;
        var today = now.Date;
        var week = await _repository.CountLettersSinceAsync(TenantId, today.AddDays(-6), ct);
        var day = await _repository.CountLettersSinceAsync(TenantId, today, ct);
        long Get(IReadOnlyDictionary<LetterStatus, long> d, LetterStatus s) => d.TryGetValue(s, out var n) ? n : 0;
        return Ok(new LetterSummary(
            Get(day, LetterStatus.Rendered),
            Get(week, LetterStatus.Rendered),
            Get(week, LetterStatus.CheckFailed),
            now));
    }

    /// <summary>Letters about one subject, newest first.</summary>
    [HttpGet("letters")]
    [RequirePermission(Read)]
    public async Task<IActionResult> ListLetters([FromQuery] string subjectKind, [FromQuery] string subjectId, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (!LetterAccess.IsKnownSubjectKind(subjectKind ?? "") || string.IsNullOrWhiteSpace(subjectId))
            return Problem400("subjectKind and subjectId are required.");
        if (!LetterAccess.CanReadSubject(_actor, subjectKind!))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"Needs {LetterAccess.SubjectReadPermission[subjectKind!]}." });
        return Ok(await _repository.ListLettersForSubjectAsync(TenantId, LetterAccess.CanonicalKind(subjectKind!), subjectId.Trim(), limit, ct));
    }

    /// <summary>The stored PDF, opened through the document catalog from one of the letter's subjects (the catalog audits the access).</summary>
    [HttpGet("letters/{letterId}/content")]
    [RequirePermission(Read)]
    public async Task<IActionResult> GetLetterContent(string letterId, CancellationToken ct)
    {
        var letter = await _repository.GetLetterAsync(TenantId, letterId, ct);
        if (letter is null || letter.DocumentId is null || !LetterAccess.CanRead(_actor, letter)) return NotFound();
        var subject = letter.Subjects.First(s => LetterAccess.CanReadSubject(_actor, s.Kind));
        var stream = await _documents.OpenAsync(TenantId, _actor.UserId, letter.DocumentId, subject.Kind, subject.Id, ct);
        return stream is null ? NotFound() : File(stream, "application/pdf", $"{letter.Id}.pdf");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private async Task<IReadOnlyCollection<string>> LetterTypeCodesAsync(CancellationToken ct) =>
        (await _repository.ListLetterTypesAsync(TenantId, ct)).Select(t => t.Code).ToHashSet(StringComparer.Ordinal);

    private LetterTemplate FromRequest(TemplateContentRequest r, string templateId, int version) => new()
    {
        Id = LetterTemplate.MakeId(templateId, version),
        TenantId = TenantId,
        TemplateId = templateId,
        Version = version,
        LetterTypeCode = r.LetterTypeCode?.Trim().ToUpperInvariant() ?? string.Empty,
        Name = r.Name?.Trim() ?? string.Empty,
        Kind = r.Kind,
        Status = TemplateStatus.Draft,
        FormNumber = r.FormNumber,
        OmbNumber = r.OmbNumber,
        FormExpiresOn = r.FormExpiresOn,
        Scope = r.Scope ?? new TemplateScope(),
        Language = r.Language?.Trim() ?? "en",
        Format = r.Format,
        Translation = r.Translation,
        EffectiveFrom = r.EffectiveFrom == default ? DateOnly.FromDateTime(Now) : r.EffectiveFrom,
        EffectiveTo = r.EffectiveTo,
        Blocks = (r.Blocks ?? []).Select(b => new TemplateBlock
        {
            Id = b.Id?.Trim() ?? string.Empty, Kind = b.Kind, Text = b.Text ?? string.Empty,
            Locked = b.Locked, FontSize = b.FontSize, Bold = b.Bold,
        }).ToList(),
        RequiredBlocks = r.RequiredBlocks ?? [],
        Attachments = (r.Attachments ?? []).Select(a => a.Trim().ToUpperInvariant()).ToList(),
        BodyFontPt = r.BodyFontPt,
        MinFontPt = r.MinFontPt,
        ReadingLevelMax = r.ReadingLevelMax,
        CreatedAt = Now,
        CreatedBy = _actor.UserId,
    };

    private ObjectResult Invalid(IEnumerable<string> errors) =>
        UnprocessableEntity(new { error = "The request is not valid.", errors });

    private BadRequestObjectResult Problem400(string error) => BadRequest(new { error });
}

/// <summary>Letter counts for the correspondence page.</summary>
public sealed record LetterSummary(long RenderedToday, long RenderedLast7Days, long CheckFailedLast7Days, DateTime AsOf);
