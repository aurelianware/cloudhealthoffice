using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CloudHealthOffice.Infrastructure.Logging;
using CorrespondenceService.Models;
using CorrespondenceService.Repositories;

namespace CorrespondenceService.Services;

public enum ComposeOutcome { Rendered, CheckFailed, Invalid, NoTemplate, StorageFailed }

public sealed record ComposeResult(ComposeOutcome Outcome, Letter? Letter, IReadOnlyList<string> Errors)
{
    public static ComposeResult Fail(ComposeOutcome outcome, params string[] errors) => new(outcome, null, errors);
}

/// <summary>
/// Turns a letter request into a frozen letter (§6 steps 2-4): resolve the
/// template, gather the merge data, set the letter date, render, check, hash
/// the data, and store the PDF write-once in the document catalog linked to
/// every subject. A letter whose checks fail is recorded with its checks and
/// nothing is stored; a missing template fails loudly.
/// </summary>
public sealed partial class LetterComposer
{
    private readonly ICorrespondenceRepository _repository;
    private readonly ILetterDocumentStore _documents;
    private readonly TimeProvider _clock;
    private readonly ILogger<LetterComposer> _logger;

    public LetterComposer(ICorrespondenceRepository repository, ILetterDocumentStore documents, TimeProvider clock, ILogger<LetterComposer> logger)
    {
        _repository = repository;
        _documents = documents;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ComposeResult> ComposeAsync(string tenantId, string actorId, LetterRequest request, CancellationToken ct = default)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
            return ComposeResult.Fail(ComposeOutcome.Invalid, errors.ToArray());

        var letterType = await _repository.GetLetterTypeAsync(tenantId, request.LetterTypeCode, ct);
        if (letterType is null)
            return ComposeResult.Fail(ComposeOutcome.Invalid, $"Letter type '{request.LetterTypeCode}' does not exist.");

        var now = _clock.GetUtcNow().UtcDateTime;
        var letterDate = request.LetterDate ?? DateOnly.FromDateTime(now);
        var templateRequest = new TemplateRequest(request.LetterTypeCode, request.Language, request.Format, letterDate,
            request.LineOfBusiness, request.State, request.Product, request.PlanId);

        var resolution = TemplateResolver.Resolve(
            await _repository.ListApprovedAsync(tenantId, request.LetterTypeCode, request.Language, request.Format, ct), templateRequest);
        if (resolution.Template is null)
        {
            _logger.LogWarning("No template for letter type {LetterType} in tenant {TenantId}: {Error}",
                LogSanitizer.Sanitize(request.LetterTypeCode), LogSanitizer.Sanitize(tenantId), LogSanitizer.Sanitize(resolution.Error));
            return ComposeResult.Fail(ComposeOutcome.NoTemplate, resolution.Error!);
        }
        var template = resolution.Template;

        var (attachments, missingAttachments) = await ResolveAttachmentsAsync(tenantId, template, templateRequest, ct);

        var letterId = "LTR-" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        var subjects = request.Subjects
            .Select(s => new LetterSubject { Kind = LetterAccess.CanonicalKind(s.Kind.Trim()), Id = s.Id.Trim() })
            .DistinctBy(s => (s.Kind, s.Id))
            .ToList();
        var model = BuildModel(request, letterType, letterId, letterDate);
        var render = LetterRenderer.Render(new LetterRenderInput(
            template, attachments, missingAttachments, model, request.Recipient, letterId, letterDate, now));

        var snapshot = Snapshot(template, attachments, request, letterDate);
        var letter = new Letter
        {
            Id = letterId,
            TenantId = tenantId,
            LetterTypeCode = letterType.Code,
            TemplateId = template.TemplateId,
            TemplateVersion = template.Version,
            Language = template.Language,
            Format = template.Format,
            Subjects = subjects,
            Recipient = request.Recipient,
            Trigger = new LetterTrigger { Event = "Manual", RequestedBy = actorId, Reason = request.Reason },
            RuleRef = request.RuleRef,
            DueBy = request.DueBy,
            LetterDate = letterDate,
            DataSnapshot = snapshot,
            DataSnapshotHash = LetterRenderer.Sha256(snapshot),
            PageCount = render.PageCount,
            Checks = render.Checks.ToList(),
            CreatedAt = now,
            Events = [Event("Requested", now, "user", $"by {actorId}" + (request.Reason is null ? "" : $": {request.Reason}"))],
        };

        if (!render.Passed)
        {
            letter.Status = LetterStatus.CheckFailed;
            letter.Events.Add(Event("CheckFailed", now, "correspondence-service",
                string.Join("; ", render.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}"))));
            await _repository.CreateLetterAsync(letter, ct);
            return new ComposeResult(ComposeOutcome.CheckFailed, letter, render.Checks.Where(c => !c.Passed).Select(c => c.Detail ?? c.Name).ToList());
        }

        StoredLetterDocument stored;
        try
        {
            stored = await _documents.StoreAsync(new LetterDocumentUpload(
                tenantId, actorId,
                subjects.Select(s => (s.Kind, s.Id)).ToList(),
                letterType.DocumentType,
                letterType.Name,
                render.Pdf,
                $"{letterId}.pdf",
                request.State,
                new Dictionary<string, string> { ["LetterId"] = letterId }), ct);
        }
        catch (DocumentCatalogException ex)
        {
            _logger.LogError(ex, "Storing letter {LetterId} in the document catalog failed", letterId);
            var refused = ex.StatusCode is System.Net.HttpStatusCode.Forbidden;
            return ComposeResult.Fail(ComposeOutcome.StorageFailed, refused
                ? "The document catalog refused the letter: the requester cannot write documents for every subject of the letter."
                : "The letter could not be stored in the document catalog; nothing was sent. Try again.");
        }

        letter.Status = LetterStatus.Rendered;
        letter.DocumentId = stored.DocumentId;
        letter.DocumentSha256 = stored.Sha256;
        letter.Events.Add(Event("Rendered", now, "correspondence-service",
            $"template {template.TemplateId} v{template.Version}, {render.PageCount} page(s), document {stored.DocumentId}"));
        try
        {
            await _repository.CreateLetterAsync(letter, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The PDF is in the catalog (linked to the subjects, with LetterId)
            // but the letter record is not: log enough to reconcile by hand.
            _logger.LogError(ex, "Letter {LetterId} was stored as document {DocumentId} but its record could not be saved",
                letterId, LogSanitizer.Sanitize(stored.DocumentId));
            throw;
        }
        return new ComposeResult(ComposeOutcome.Rendered, letter, []);
    }

    /// <summary>Renders a template version against sample data; nothing is stored.</summary>
    public async Task<LetterRenderResult> PreviewAsync(string tenantId, LetterTemplate template, PreviewRequest request, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var letterDate = request.LetterDate ?? DateOnly.FromDateTime(now);
        var letterType = await _repository.GetLetterTypeAsync(tenantId, template.LetterTypeCode, ct)
            ?? new LetterType { Code = template.LetterTypeCode, Name = template.Name };
        var templateRequest = new TemplateRequest(template.LetterTypeCode, template.Language, template.Format, letterDate,
            template.Scope.LineOfBusiness, template.Scope.State, template.Scope.Product, template.Scope.PlanId);
        var (attachments, missing) = await ResolveAttachmentsAsync(tenantId, template, templateRequest, ct);
        var model = BuildModel(new LetterRequest { Data = request.Data, Recipient = request.Recipient }, letterType, "PREVIEW", letterDate);

        // A draft is previewed as if approved, so the locked-text check speaks
        // to what approval would freeze.
        var rendered = template.Status == TemplateStatus.Draft ? Approved(template) : template;
        return LetterRenderer.Render(new LetterRenderInput(rendered, attachments, missing, model, request.Recipient, "PREVIEW", letterDate, now));
    }

    private async Task<(List<LetterTemplate> Found, List<string> Missing)> ResolveAttachmentsAsync(
        string tenantId, LetterTemplate template, TemplateRequest request, CancellationToken ct)
    {
        var found = new List<LetterTemplate>();
        var missing = new List<string>();
        foreach (var code in template.Attachments)
        {
            var resolved = TemplateResolver.Resolve(
                await _repository.ListApprovedAsync(tenantId, code, request.Language, request.Format, ct),
                request with { LetterTypeCode = code });
            if (resolved.Template is null) missing.Add(code);
            else found.Add(resolved.Template);
        }
        return (found, missing);
    }

    internal static Dictionary<string, object?> BuildModel(LetterRequest request, LetterType letterType, string letterId, DateOnly letterDate)
    {
        var model = MergeFields.FromJson(request.Data);
        // Filled by the service, never by the caller.
        model["letter"] = new Dictionary<string, object?>
        {
            ["id"] = letterId,
            ["date"] = letterDate.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture),
            ["isoDate"] = letterDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["type"] = letterType.Code,
            ["typeName"] = letterType.Name,
        };
        var r = request.Recipient;
        model["recipient"] = new Dictionary<string, object?>
        {
            ["name"] = r.Name,
            ["addressLine1"] = r.AddressLine1,
            ["addressLine2"] = r.AddressLine2,
            ["city"] = r.City,
            ["state"] = r.State,
            ["postalCode"] = r.PostalCode,
            ["party"] = r.Party.ToString(),
        };
        return model;
    }

    /// <summary>The merge data, recipient and template exactly as rendered, as canonical JSON (keys sorted).</summary>
    internal static string Snapshot(LetterTemplate template, IReadOnlyList<LetterTemplate> attachments, LetterRequest request, DateOnly letterDate)
    {
        var node = new JsonObject
        {
            ["letterType"] = template.LetterTypeCode,
            ["template"] = $"{template.TemplateId}:v{template.Version}",
            ["attachments"] = new JsonArray(attachments.Select(a => (JsonNode)JsonValue.Create($"{a.TemplateId}:v{a.Version}")).ToArray()),
            ["language"] = template.Language,
            ["format"] = template.Format.ToString(),
            ["letterDate"] = letterDate.ToString("yyyy-MM-dd"),
            ["recipient"] = JsonSerializer.SerializeToNode(request.Recipient),
            ["data"] = request.Data?.DeepClone(),
        };
        return Canonical(node)!.ToJsonString();
    }

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Canonical(p.Value?.DeepClone())))),
        JsonArray a => new JsonArray(a.Select(x => Canonical(x?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    private static LetterTemplate Approved(LetterTemplate draft)
    {
        var copy = JsonSerializer.Deserialize<LetterTemplate>(JsonSerializer.Serialize(draft))!;
        TemplateRules.StampApproval(copy, "preview", DateTime.UtcNow);
        return copy;
    }

    private static LetterEvent Event(string type, DateTime at, string source, string? detail) =>
        new() { Type = type, OccurredAt = at, RecordedAt = at, Source = source, Detail = detail };

    internal static List<string> Validate(LetterRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.LetterTypeCode)) errors.Add("LetterTypeCode is required.");
        if (request.Subjects.Count == 0) errors.Add("A letter needs at least one subject (the member, claim, appeal... it is about).");
        if (request.Subjects.Count > 10) errors.Add("A letter has at most 10 subjects.");
        foreach (var s in request.Subjects)
        {
            if (!LetterAccess.IsKnownSubjectKind(s.Kind?.Trim() ?? string.Empty))
                errors.Add($"Unknown subject kind '{s.Kind}'.");
            if (string.IsNullOrWhiteSpace(s.Id) || !SubjectId().IsMatch(s.Id.Trim()))
                errors.Add($"Subject id '{s.Id}' is not valid.");
        }
        if (request.Recipient is null) errors.Add("Recipient is required.");
        if (request.Data is not null && (request.Data.ContainsKey("letter") || request.Data.ContainsKey("recipient")))
            errors.Add("Data cannot set 'letter' or 'recipient'; the service fills them.");
        return errors;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$")]
    private static partial Regex SubjectId();
}
