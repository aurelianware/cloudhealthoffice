using CloudHealthOffice.Infrastructure.Contacts;
using CloudHealthOffice.Infrastructure.Security;
using ContactService.Models;
using ContactService.Repositories;
using ContactService.Services;
using Microsoft.AspNetCore.Mvc;

namespace ContactService.Controllers;

[RequirePermission(ChoRolePermissions.ServiceContact)]
[ApiController, Route("api/contacts/{id}")]
public sealed class ContactWorkflowController(
    IContactRepository contacts, IContactWorkflowGateway gateway, ICurrentActor actor) : ControllerBase
{
    [HttpPost("case")]
    public async Task<ActionResult<Contact>> OpenCase(string id, OpenContactCaseRequest request, CancellationToken ct)
    {
        var c = await contacts.GetByIdAsync(id, ct);
        if (c is null) return NotFound();
        if (c.OpenedCase is not null) return Ok(c);
        if (c.Status != ContactStatus.Open) return Conflict("Reopen the contact before opening a case.");
        if (c.Revision != request.Revision) return Conflict("Reload the contact before retrying.");
        if (c.Classification is null or ContactClassification.Inquiry)
            return BadRequest("Save a case classification first.");
        if (c.Caller.Type != CallerType.Member || string.IsNullOrWhiteSpace(c.MemberId))
            return BadRequest("This intake supports identified members acting for themselves. Representative and provider tracks require separate intake.");
        if (string.IsNullOrWhiteSpace(c.Summary)) return BadRequest("Save the member's request in the summary first.");
        if (c.Classification == ContactClassification.CoverageRequest && request.Intent.Coverage is null && c.CaseIntent is null)
            return BadRequest("Coverage requests require clinical details.");

        try
        {
            if (c.CaseIntent is null)
            {
                c.CaseIntent = request.Intent;
                ContactLifecycle.Record(c, "CaseIntakeRequested", actor.UserId, DateTime.UtcNow);
                c = await contacts.UpdateAsync(c, ct); // Fence before any remote side effect.
            }
            // Persist an unknown outcome BEFORE dispatch. A stale response must not
            // overwrite a newer attempt/amendment. The same owner identity is reused.
            c.CaseDeliveryStatus = "Unknown";
            ContactLifecycle.Record(c, "CaseDeliveryStarted", actor.UserId, DateTime.UtcNow);
            c = await contacts.UpdateAsync(c, ct);
            var attemptRevision = c.Revision;
            ContactCaseResult result;
            try { result = await gateway.OpenCaseAsync(c, ct); }
            catch (ContactWorkflowUnavailableException ex) when (ex.Status is 400 or 422)
            {
                c.CaseDeliveryStatus = "Rejected";
                ContactLifecycle.Record(c, "CaseDeliveryRejected", actor.UserId, DateTime.UtcNow);
                await contacts.UpdateAsync(c, ct);
                throw;
            }
            var expectedId = ContactIntakeIdentity.For(actor.TenantId, c.Id);
            if (result.Id != expectedId || result.ReceivedAt != c.StartedAt)
                return StatusCode(502, "The owning service returned an inconsistent receipt. Intake remains pending.");
            // Reload so unrelated edits made during delivery are preserved.
            c = await contacts.GetByIdAsync(id, ct) ?? throw new InvalidOperationException("Contact disappeared.");
            if (c.Revision != attemptRevision) return Conflict("A newer intake attempt exists. Reload and reconcile using the saved request.");
            c.CaseDeliveryStatus = result.IntakeHash == ContactIntakeIdentity.Hash(c.CaseIntent!)
                ? "Accepted" : "ReconciledPriorIntent";
            c.OpenedCase ??= result;
            ContactLifecycle.Record(c, "CaseOpened", actor.UserId, DateTime.UtcNow, result.Id);
            return Ok(await contacts.UpdateAsync(c, ct));
        }
        catch (ContactConcurrencyException) { return Conflict("The contact changed. Reload and retry; the same case identity will be reused."); }
        catch (ContactWorkflowUnavailableException ex) { return StatusCode(ex.Status is 400 or 403 or 409 or 422 ? ex.Status : 503, ex.Message); }
        catch (HttpRequestException) { return StatusCode(503, "Case intake is pending. Reload and retry when the owning service is available."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return StatusCode(503, "Case intake timed out. Reload and retry the saved request."); }
    }

    [HttpPost("case/amend")]
    public async Task<ActionResult<Contact>> AmendCase(string id, AmendContactCaseRequest request, CancellationToken ct)
    {
        var c = await contacts.GetByIdAsync(id, ct);
        if (c is null) return NotFound();
        if (c.Revision != request.Revision) return Conflict("Reload before amending intake.");
        if (c.Status != ContactStatus.Open || c.OpenedCase is not null || c.CaseIntent is null
            || c.CaseDeliveryStatus != "Rejected")
            return Conflict("Only rejected, unopened intake can be amended. Reconcile unknown outcomes by retrying first.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000)
            return BadRequest("An amendment reason of at most 1000 characters is required.");
        if (c.CaseAmendments.Count >= 100) return Conflict("Intake amendment limit reached; escalate to the case owner.");
        if (c.Classification == ContactClassification.CoverageRequest && request.Intent.Coverage is null)
            return BadRequest("Coverage requests require clinical details.");
        c.CaseAmendments.Add(new ContactCaseAmendment { PreviousIntent = c.CaseIntent,
            Reason = request.Reason.Trim(), By = actor.UserId, At = DateTime.UtcNow });
        c.CaseIntent = request.Intent;
        c.CaseDeliveryStatus = "Pending";
        ContactLifecycle.Record(c, "CaseIntakeAmended", actor.UserId, DateTime.UtcNow, request.Reason.Trim());
        try { return Ok(await contacts.UpdateAsync(c, ct)); }
        catch (ContactConcurrencyException) { return Conflict("The contact changed. Reload before amending intake."); }
    }

    [HttpPost("follow-ups")]
    public async Task<ActionResult<Contact>> FollowUp(string id, CreateContactFollowUpRequest request, CancellationToken ct)
    {
        if (!Guid.TryParseExact(request.Key, "N", out _)) return BadRequest("A stable follow-up request key is required.");
        var c = await contacts.GetByIdAsync(id, ct);
        if (c is null) return NotFound();
        var intent = c.FollowUps.FirstOrDefault(f => f.Key == request.Key);
        if (intent?.WorkItemId is not null) return Ok(c);
        if (c.Status != ContactStatus.Open) return Conflict("Reopen the contact before creating follow-up work.");
        if (c.Revision != request.Revision) return Conflict("Reload the contact before retrying.");
        if (intent is null && (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 500))
            return BadRequest("Describe the follow-up in at most 500 characters.");
        if (intent is null && (request.DueAt is null || request.DueAt <= DateTime.UtcNow || request.DueAt > DateTime.UtcNow.AddYears(2)))
            return BadRequest("Choose a follow-up due date within the next two years.");
        if (intent is null && c.FollowUps.Count >= 100) return BadRequest("This contact already has 100 follow-ups.");
        try
        {
            if (intent is null)
            {
                intent = new ContactFollowUpIntent { Key = request.Key, Title = request.Title.Trim(), DueAt = request.DueAt,
                    RequestedAt = DateTime.UtcNow, RequestedBy = actor.UserId, MemberId = c.MemberId,
                    Channel = c.Channel.ToString(), CallerType = c.Caller.Type.ToString(),
                    Classification = c.Classification?.ToString() ?? "Unclassified" };
                c.FollowUps.Add(intent);
                ContactLifecycle.Record(c, "FollowUpRequested", actor.UserId, DateTime.UtcNow, intent.Key);
                c = await contacts.UpdateAsync(c, ct);
            }
            var workId = await gateway.CreateFollowUpAsync(id, request.Key, ct);
            c = await contacts.GetByIdAsync(id, ct) ?? throw new InvalidOperationException("Contact disappeared.");
            c.FollowUps.Single(f => f.Key == request.Key).WorkItemId = workId;
            if (!c.FollowUpWorkItemIds.Contains(workId)) c.FollowUpWorkItemIds.Add(workId);
            ContactLifecycle.Record(c, "FollowUpCreated", actor.UserId, DateTime.UtcNow, workId);
            return Ok(await contacts.UpdateAsync(c, ct));
        }
        catch (ContactConcurrencyException) { return Conflict("The contact changed. Reload and retry the saved follow-up."); }
        catch (ContactWorkflowUnavailableException ex) { return StatusCode(ex.Status is 400 or 403 or 409 or 422 ? ex.Status : 503, ex.Message); }
        catch (HttpRequestException) { return StatusCode(503, "Follow-up delivery is pending. Reload and retry."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return StatusCode(503, "Follow-up delivery timed out. Reload and retry."); }
    }
}

public sealed class OpenContactCaseRequest
{
    public long Revision { get; set; }
    public ContactCaseIntent Intent { get; set; } = new();
}
public sealed class CreateContactFollowUpRequest
{
    public long Revision { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime? DueAt { get; set; }
}

public sealed class AmendContactCaseRequest
{
    public long Revision { get; set; }
    public string Reason { get; set; } = string.Empty;
    public ContactCaseIntent Intent { get; set; } = new();
}
