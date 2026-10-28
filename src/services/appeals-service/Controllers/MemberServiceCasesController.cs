using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using AppealsService.Services.RulePacks;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Contacts;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace AppealsService.Controllers;

[ApiController, Route("api/service-cases")]
public sealed class MemberServiceCasesController(
    IMemberServiceCaseStore store, IContactIntakeReader contacts, ICurrentActor actor,
    IRulePackCatalog catalog, IAppealHolidayCalendarProvider holidays, IAppealFieldEncryptor encryptor,
    IMemberCaseSubjectValidator subjects, AppealDecisionReporting reporting) : ControllerBase
{
    [HttpGet("{id}")]
    [RequirePermission(ChoRolePermissions.ServiceRead)]
    [AuditRecordAccess("MemberServiceCase", "View", RecordIdRouteValue = "id")]
    public async Task<ActionResult<ContactCaseResult>> Get(string id, CancellationToken ct)
    {
        var row = await store.GetAsync(actor.TenantId, id, ct);
        return row is null ? NotFound() : Ok(row.Receipt);
    }

    [HttpGet]
    [RequirePermission("appeals:read")]
    [AuditRecordAccess("MemberServiceCase", "Search")]
    public async Task<ActionResult> List(CancellationToken ct) =>
        Ok((await store.ListAsync(actor.TenantId, ct)).Select(r => r.Receipt));

    [HttpGet("{id}/review")]
    [RequirePermission("appeals:read")]
    [AuditRecordAccess("MemberServiceCase", "View", RecordIdRouteValue = "id")]
    public async Task<ActionResult> Review(string id, CancellationToken ct)
    {
        var row = await store.GetAsync(actor.TenantId, id, ct);
        return row is null ? NotFound() : Ok(new {
            row.Id, row.MemberId, row.ContactId, row.AuthorizationId, row.Classification, row.Status,
            row.Revision, row.ReceivedAt, row.Receipt.DueAt, row.Receipt.RulePackVersion,
            Summary = await encryptor.DecryptAsync(row.EncryptedSummary, ct),
            Resolution = await encryptor.DecryptAsync(row.EncryptedResolution, ct),
            row.DecisionRecordedAt, row.DecisionRecordedBy, row.DecisionOutcome,
            Reporting = ReportingState(row), row.ReportedToAuthorizationAt, row.ReportingRejectedReason,
            RuleEvaluation = JsonSerializer.Deserialize<JsonElement>(row.PinnedRuleEvaluationJson)
        });
    }

    [HttpPost("{id}/decision")]
    [RequirePermission("appeals:write")]
    public async Task<ActionResult> RecordDecision(string id, MemberCaseDecisionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Resolution) || request.Resolution.Length > 4000)
            return BadRequest("Record the review outcome in at most 4000 characters.");
        var row = await store.GetAsync(actor.TenantId, id, ct);
        if (row is null) return NotFound();
        var appeal = row.Classification.EndsWith("Appeal", StringComparison.Ordinal);
        if (appeal && (request.Outcome is not { } outcome || !Enum.IsDefined(outcome)))
            return BadRequest("Record whether the appeal upheld, overturned or partially overturned the denial.");
        if (!appeal && request.Outcome is not null)
            return BadRequest("A grievance has no appeal outcome.");
        var encrypted = await encryptor.EncryptAsync(request.Resolution, ct) ?? throw new InvalidOperationException("Resolution encryption failed.");
        var at = DateTime.UtcNow;
        if (!await store.RecordDecisionAsync(actor.TenantId, id, request.Revision, encrypted, actor.UserId, at,
                appeal ? request.Outcome : null, ct))
            return Conflict("The case changed or already has a recorded decision. Reload it before continuing.");
        if (appeal && !string.IsNullOrWhiteSpace(row.AuthorizationId))
        {
            // CMS-0057-F: put the outcome on the authorization's reporting ledger now; whatever
            // does not land here is retried by AppealDecisionReportingSweep. The decision stands either way.
            row.Status = "DecisionRecorded"; row.DecisionOutcome = request.Outcome; row.DecisionRecordedAt = at;
            await reporting.DeliverAsync(row, ct);
        }
        return NoContent();
    }

    /// <summary>Where the appeal outcome stands on the authorization's PA reporting ledger.</summary>
    internal static string? ReportingState(MemberServiceCase row) =>
        row.DecisionOutcome is null || string.IsNullOrWhiteSpace(row.AuthorizationId) ? null
        : row.ReportedToAuthorizationAt is not null ? "Reported"
        : row.ReportingRejectedReason is not null ? "NeedsAttention"
        : "Pending";

    [HttpPost("from-contact/{contactId}")]
    [RequirePermission(ChoRolePermissions.ServiceContact)]
    public async Task<ActionResult<ContactCaseResult>> Create(string contactId, CancellationToken ct)
    {
        ContactIntakeReceipt? c;
        try { c = await contacts.GetAsync(contactId, ct); }
        catch (HttpRequestException) { return StatusCode(503, "Contact receipt is unavailable; no new case was created."); }
        if (c is null || c.Id != contactId || c.TenantId != actor.TenantId) return NotFound();
        if (c.CaseIntent is not { } intent || string.IsNullOrWhiteSpace(c.MemberId)
            || c.Caller.Type != "Member" || string.IsNullOrWhiteSpace(c.Summary))
            return BadRequest("A saved member-self intake intent is required.");
        if (c.Classification is not ("Grievance" or "ExpeditedGrievance" or "Appeal" or "ExpeditedAppeal"))
            return BadRequest("This intake accepts member grievances and appeals only.");
        if (c.StartedAt == default || c.StartedAt > DateTime.UtcNow.AddMinutes(5))
            return BadRequest("The contact receipt time is invalid.");
        var id = ContactIntakeIdentity.For(actor.TenantId, c.Id);
        if (await store.GetAsync(actor.TenantId, id, ct) is { } existing) return Ok(existing.Receipt);
        if (!Enum.TryParse<LineOfBusiness>(intent.LineOfBusiness, out var lob)
            || !Enum.IsDefined(lob)) return BadRequest("Unknown line of business.");
        var pack = catalog.Find(intent.State, lob, DateOnly.FromDateTime(c.StartedAt));
        if (pack is null) return UnprocessableEntity("No effective rule pack is available for this jurisdiction and line of business.");
        var isAppeal = c.Classification.EndsWith("Appeal", StringComparison.Ordinal);
        if (isAppeal)
        {
            try
            {
                if (!await subjects.IsPreServiceMedicalDenialAsync(intent.AuthorizationId, c.MemberId, c.StartedAt, ct))
                    return UnprocessableEntity("This intake supports pre-service medical authorization appeals only. A matching denied authorization is required; payment and drug appeals use separate intake.");
            }
            catch (HttpRequestException) { return StatusCode(503, "The authorization subject could not be verified."); }
        }
        var expedited = c.Classification.StartsWith("Expedited", StringComparison.Ordinal);
        var grievance = c.Classification.EndsWith("Grievance", StringComparison.Ordinal);
        var evaluation = RulePackEvaluator.Evaluate(pack, new RulePackEvaluationInput {
            ReceivedAt = c.StartedAt, Expedited = expedited,
            CaseType = grievance ? RulePackCaseType.MemberGrievance : RulePackCaseType.MemberAppeal
        }, holidays.ForTenant(actor.TenantId));
        var receipt = new ContactCaseResult { Id = id, Kind = grievance ? "Grievance" : "Appeal",
            Status = "Received", ReceivedAt = c.StartedAt, IntakeHash = ContactIntakeIdentity.Hash(intent),
            DueAt = evaluation.Deadlines.FirstOrDefault(d => d.Name == "resolution")?.DueAt,
            RulePackVersion = evaluation.PackId + ":" + evaluation.Version };
        var record = new MemberServiceCase { Id = id, TenantId = actor.TenantId, ContactId = c.Id,
            MemberId = c.MemberId, AuthorizationId = intent.AuthorizationId, Classification = c.Classification, Channel = c.Channel,
            ReceivedAt = c.StartedAt, CreatedAt = DateTime.UtcNow, CreatedBy = actor.UserId,
            EncryptedSummary = await encryptor.EncryptAsync(c.Summary, ct) ?? string.Empty,
            PinnedRuleEvaluationJson = JsonSerializer.Serialize(evaluation), Receipt = receipt };
        var saved = await store.CreateOrReadAsync(record, ct);
        return Ok(saved.Receipt);
    }
}

public sealed class MemberCaseDecisionRequest
{
    public long Revision { get; set; }
    public string Resolution { get; set; } = string.Empty;
    /// <summary>Required for an appeal (it feeds CMS-0057-F reporting); refused for a grievance.</summary>
    public MemberAppealOutcome? Outcome { get; set; }
}
