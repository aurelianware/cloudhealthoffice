using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Security;
using ContactService.Models;
using ContactService.Repositories;
using ContactService.Services;
using ContactService.Services.Claims;
using Microsoft.AspNetCore.Mvc;

namespace ContactService.Controllers;

/// <summary>
/// Contacts: the record of a call, chat, email or walk-in, and the screen a
/// service representative works in.
///
/// The tenant and the acting rep come from the validated CHO token only;
/// request bodies never name either. Reads need <c>service:read</c> and
/// writes <c>service:contact</c> (the defaults set in Program.cs).
///
/// See docs/architecture/cho-service.md §3 and §6.
/// </summary>
[ApiController]
[Route("api/contacts")]
[Produces("application/json")]
public sealed class ContactsController(
    IContactRepository contacts,
    IContactReasonRepository reasons,
    IContactNoteGateway notes,
    ICurrentActor currentActor,
    ILogger<ContactsController> logger,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private string Actor => currentActor.UserId;
    private string? ActorName => string.IsNullOrWhiteSpace(currentActor.DisplayName) ? null : currentActor.DisplayName;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : currentActor.TenantId;

    // ── Reading ───────────────────────────────────────────────────

    [HttpGet("{id}")]
    [AuditRecordAccess("Contact", "View", RecordIdRouteValue = "id")]
    [ProducesResponseType(typeof(Contact), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Contact>> GetById(string id, CancellationToken ct) =>
        await contacts.GetByIdAsync(id, ct) is { } contact ? Ok(contact) : NotFound();

    /// <summary>A member's contact history, newest first.</summary>
    [HttpGet("by-member/{memberId}")]
    [AuditRecordAccess("Contact", "Member", MemberIdRouteValue = "memberId")]
    [ProducesResponseType(typeof(IReadOnlyList<ContactSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ContactSummary>>> ListByMember(
        string memberId, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        Ok((await contacts.ListByMemberAsync(memberId, limit, ct)).Select(Summarize).ToList());

    /// <summary>A provider's contact history, newest first.</summary>
    [HttpGet("by-provider/{npi}")]
    [AuditRecordAccess("Contact", "Provider")]
    [ProducesResponseType(typeof(IReadOnlyList<ContactSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ContactSummary>>> ListByProvider(
        string npi, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        Ok((await contacts.ListByProviderAsync(npi, limit, ct)).Select(Summarize).ToList());

    /// <summary>
    /// Who called about this claim, authorization or appeal, and when. This
    /// is what the claim screen asks for.
    /// </summary>
    [HttpGet("by-subject/{type}/{subjectId}")]
    [AuditRecordAccess("Contact", "Subject")]
    [ProducesResponseType(typeof(IReadOnlyList<ContactSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ContactSummary>>> ListBySubject(
        ContactSubjectType type, string subjectId, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        Ok((await contacts.ListBySubjectAsync(type, subjectId, limit, ct)).Select(Summarize).ToList());

    /// <summary>
    /// The signed-in rep's own recent contacts, or just the ones they left
    /// open — which is how they find the contact they were half-way through
    /// when the next call arrived.
    /// </summary>
    [HttpGet("mine")]
    [ProducesResponseType(typeof(IReadOnlyList<ContactSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ContactSummary>>> ListMine(
        [FromQuery] bool openOnly = false, [FromQuery] int limit = 50, CancellationToken ct = default) =>
        Ok((await contacts.ListByAgentAsync(Actor, openOnly, limit, ct)).Select(Summarize).ToList());

    // ── Working a contact ─────────────────────────────────────────

    /// <summary>
    /// Starts a contact. A rep can start one before they know who is calling:
    /// the member, the reasons and what was discussed are all filled in as
    /// the conversation goes.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Contact), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Contact>> Start(
        [FromBody] StartContactRequest request, CancellationToken ct)
    {
        var now = Now;
        var startedAt = request.StartedAt ?? now;

        if (ContactLifecycle.WhyNotStartable(startedAt, now) is { } why) return BadRequest(why);

        var (reasonCodes, unknown) = await SiftAsync(request.ReasonCodes, ct);
        if (unknown.Count > 0)
            logger.LogInformation(
                "{Count} reason code(s) on a new contact are not in this tenant's taxonomy and were dropped.",
                unknown.Count);

        var contact = new Contact
        {
            ContactNumber      = request.ContactNumber ?? ContactLifecycle.NextContactNumber(now),
            Channel            = request.Channel,
            Direction          = request.Direction,
            Caller             = request.Caller,
            MemberId           = request.MemberId,
            MemberVerification = request.MemberVerification,
            ProviderNpi        = request.ProviderNpi,
            ProviderName       = request.ProviderName,
            StartedAt          = startedAt,
            PhoneCallId        = request.PhoneCallId,
            Queue              = request.Queue,
            ReasonCodes        = reasonCodes,
            AgentId            = Actor,
            AgentName          = ActorName,
            Status             = ContactStatus.Open,
            CreatedBy          = Actor,
            UpdatedBy          = Actor,
        };

        ContactLifecycle.Record(contact, ContactLifecycle.Actions.Started, Actor, now,
            $"{contact.Direction} {contact.Channel}");

        try
        {
            await contacts.CreateAsync(contact, ct);
        }
        catch (DuplicateContactNumberException ex)
        {
            return Conflict(ex.Message);
        }

        return CreatedAtAction(nameof(GetById), new { id = contact.Id }, contact);
    }

    /// <summary>
    /// Changes an open contact as the conversation goes: who it turned out
    /// to be, what it is about, the wrap-up text.
    /// </summary>
    [HttpPatch("{id}")]
    [ProducesResponseType(typeof(Contact), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Contact>> Update(
        string id, [FromBody] UpdateContactRequest request, CancellationToken ct)
    {
        if (await contacts.GetByIdAsync(id, ct) is not { } contact) return NotFound();
        if (ContactLifecycle.WhyNotEditable(contact) is { } why) return BadRequest(why);
        if (contact.Revision != request.Revision) return StaleRead(contact);

        var now = Now;
        var changed = new List<string>();

        if (request.MemberId is { Length: > 0 } memberId && memberId != contact.MemberId)
        {
            contact.MemberId = memberId;
            changed.Add("member");
        }

        if (request.MemberVerification is { Length: > 0 } verification && verification != contact.MemberVerification)
        {
            contact.MemberVerification = verification;
            changed.Add("verification");
        }

        if (request.ProviderNpi is { Length: > 0 } npi && npi != contact.ProviderNpi)
        {
            contact.ProviderNpi = npi;
            changed.Add("provider");
        }

        if (request.ProviderName is { Length: > 0 } providerName && providerName != contact.ProviderName)
        {
            contact.ProviderName = providerName;
            changed.Add("provider name");
        }

        if (request.Caller is { } caller && !SameCaller(contact.Caller, caller))
        {
            contact.Caller = caller;
            changed.Add("caller");
        }

        if (request.ReasonCodes is { Count: > 0 } requestedReasons)
        {
            var (accepted, _) = await SiftAsync(requestedReasons, ct);
            if (accepted.Count > 0 && !accepted.SequenceEqual(contact.ReasonCodes))
            {
                contact.ReasonCodes = accepted;
                changed.Add("reasons");
            }
        }

        if (request.Summary is { Length: > 0 } summary && summary != contact.Summary)
        {
            contact.Summary = summary;
            changed.Add("summary");
        }

        if (request.RecordingLink is { Length: > 0 } recording && recording != contact.RecordingLink)
        {
            contact.RecordingLink = recording;
            contact.RecordingPlatform = request.RecordingPlatform ?? contact.RecordingPlatform;
            changed.Add("recording");
        }

        if (request.TranscriptDocumentId is { Length: > 0 } transcript && transcript != contact.TranscriptDocumentId)
        {
            contact.TranscriptDocumentId = transcript;
            changed.Add("transcript");
        }

        if (request.Classification is { } classification)
        {
            ApplyClassification(contact, classification, now);
            changed.Add("classification");
        }

        if (changed.Count == 0) return Ok(contact);

        ContactLifecycle.Record(contact, ContactLifecycle.Actions.Updated, Actor, now, string.Join(", ", changed));

        return await SaveAsync(contact, ct);
    }

    /// <summary>
    /// Links something the caller asked about. For a claim this also writes
    /// a contact note on the claim, so the claim's own history shows the
    /// call; the link stands whether or not that note could be written.
    /// </summary>
    [HttpPost("{id}/subjects")]
    [ProducesResponseType(typeof(Contact), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Contact>> LinkSubject(
        string id, [FromBody] LinkSubjectRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SubjectId)) return BadRequest("subjectId is required");
        if (await contacts.GetByIdAsync(id, ct) is not { } contact) return NotFound();
        if (ContactLifecycle.WhyNotEditable(contact) is { } why) return BadRequest(why);
        if (contact.Revision != request.Revision) return StaleRead(contact);

        var already = contact.Subjects.FirstOrDefault(
            s => s.Type == request.Type && s.SubjectId == request.SubjectId);
        if (already is not null) return Ok(contact);

        var now = Now;

        // The note goes on the subject before the link is saved, so the id it
        // returns is stored with the link rather than needing a second write.
        var noteId = await notes.WriteContactNoteAsync(
            new ContactNoteCommand(TenantId, request.Type, request.SubjectId,
                contact.Caller.Type, contact.ContactNumber, request.Note),
            ct);

        contact.Subjects.Add(new ContactSubject
        {
            Type = request.Type,
            SubjectId = request.SubjectId,
            Reference = request.Reference,
            Note = request.Note,
            SubjectNoteId = noteId,
            LinkedAt = now,
            LinkedBy = Actor,
        });

        ContactLifecycle.Record(contact, ContactLifecycle.Actions.SubjectLinked, Actor, now,
            $"{request.Type} {request.Reference ?? request.SubjectId}");

        return await SaveAsync(contact, ct);
    }

    /// <summary>
    /// Closes the contact: the caller has hung up and the after-call work is
    /// done.
    /// </summary>
    [HttpPost("{id}/close")]
    [ProducesResponseType(typeof(Contact), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Contact>> Close(
        string id, [FromBody] CloseContactRequest request, CancellationToken ct)
    {
        if (await contacts.GetByIdAsync(id, ct) is not { } contact) return NotFound();
        if (contact.Revision != request.Revision) return StaleRead(contact);

        var now = Now;
        var endedAt = request.EndedAt ?? now;

        if (ContactLifecycle.WhyNotCloseable(contact, endedAt) is { } why) return BadRequest(why);

        if (request.Summary is { Length: > 0 } summary) contact.Summary = summary;
        if (request.Classification is { } classification) ApplyClassification(contact, classification, now);

        contact.EndedAt = endedAt;
        contact.DurationSeconds = ContactLifecycle.Duration(contact.StartedAt, endedAt);
        contact.Resolution = request.Resolution;
        contact.Status = ContactStatus.Closed;
        contact.ClosedAt = now;

        ContactLifecycle.Record(contact, ContactLifecycle.Actions.Closed, Actor, now, request.Resolution.ToString());

        return await SaveAsync(contact, ct);
    }

    /// <summary>
    /// Reopens a closed contact, because the caller rang back about the same
    /// thing or the rep closed it by mistake. The close stays in the history.
    /// </summary>
    [HttpPost("{id}/reopen")]
    [ProducesResponseType(typeof(Contact), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Contact>> Reopen(
        string id, [FromBody] ReopenContactRequest request, CancellationToken ct)
    {
        if (await contacts.GetByIdAsync(id, ct) is not { } contact) return NotFound();
        if (contact.Status != ContactStatus.Closed) return BadRequest("This contact is not closed.");
        if (contact.Revision != request.Revision) return StaleRead(contact);

        var now = Now;

        contact.Status = ContactStatus.Open;
        contact.ClosedAt = null;

        ContactLifecycle.Record(contact, ContactLifecycle.Actions.Reopened, Actor, now, request.Reason);

        return await SaveAsync(contact, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private void ApplyClassification(Contact contact, ContactClassification classification, DateTime now)
    {
        if (contact.Classification == classification) return;

        contact.Classification = classification;
        contact.ClassifiedBy = Actor;
        contact.ClassifiedAt = now;

        // Step 2 of cho-service.md §8 opens the grievance, coverage request
        // or appeal here, dated from contact.StartedAt, and starts its
        // rule-pack clock. Step 1 records the rep's answer and nothing more.
        ContactLifecycle.Record(contact, ContactLifecycle.Actions.Classified, Actor, now, classification.ToString());
    }

    /// <summary>
    /// Whether the caller details are the ones already recorded. The screen
    /// sends the whole caller on every save, so without this each Save would
    /// bump the revision and add a history line saying nothing happened.
    /// </summary>
    private static bool SameCaller(ContactCaller stored, ContactCaller incoming) =>
        stored.Type == incoming.Type
        && stored.Name == incoming.Name
        && stored.Phone == incoming.Phone
        && stored.Npi == incoming.Npi
        && stored.Relationship == incoming.Relationship
        && stored.AorOnFile == incoming.AorOnFile;

    private async Task<(List<string> Accepted, List<string> Unknown)> SiftAsync(
        IEnumerable<string> requested, CancellationToken ct)
    {
        var known = (await reasons.ListAsync(activeOnly: true, ct))
            .Select(r => r.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ContactLifecycle.SiftReasonCodes(requested, known);
    }

    private async Task<ActionResult<Contact>> SaveAsync(Contact contact, CancellationToken ct)
    {
        try
        {
            return Ok(await contacts.UpdateAsync(contact, ct));
        }
        catch (ContactConcurrencyException ex)
        {
            return Conflict(ex.Message);
        }
    }

    private ActionResult<Contact> StaleRead(Contact contact) =>
        Conflict($"This contact has changed since you read it (it is now revision {contact.Revision}). " +
                 "Reload it and try again.");

    private static ContactSummary Summarize(Contact contact) => new()
    {
        Id             = contact.Id,
        ContactNumber  = contact.ContactNumber,
        Channel        = contact.Channel,
        Direction      = contact.Direction,
        Status         = contact.Status,
        Classification = contact.Classification,
        Resolution     = contact.Resolution,
        MemberId       = contact.MemberId,
        ProviderNpi    = contact.ProviderNpi,
        CallerName     = contact.Caller.Name,
        CallerType     = contact.Caller.Type,
        ReasonCodes    = contact.ReasonCodes,
        SubjectCount   = contact.Subjects.Count,
        StartedAt      = contact.StartedAt,
        EndedAt        = contact.EndedAt,
        DurationSeconds = contact.DurationSeconds,
        AgentId        = contact.AgentId,
        AgentName      = contact.AgentName,
    };
}

/// <summary>Reopens a closed contact, with a reason for the history.</summary>
public class ReopenContactRequest
{
    public long Revision { get; set; }

    [System.ComponentModel.DataAnnotations.StringLength(500)]
    public string? Reason { get; set; }
}
