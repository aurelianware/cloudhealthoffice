using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using WorkManagementService.Domain;
using WorkManagementService.Models;
using WorkManagementService.Repositories;
using WorkManagementService.Services;

namespace WorkManagementService.Controllers;

/// <summary>
/// Work distribution: Get Next and the caller's own leases
/// (<c>workqueue:work</c>), the supervisor list and push actions
/// (<c>workqueue:assign</c>), queue counts and item detail
/// (<c>workqueue:read</c>). The tenant and the acting user are the token's,
/// never a header, query or body field.
///
/// The work service is advisory in phase 1: a lease tells people who is
/// working an item; claims-service does not check it. Its ResolutionLock
/// remains the hard guard on a claim resolution.
/// </summary>
[ApiController]
[Route("api/v1/work")]
[Produces("application/json")]
public sealed class WorkController : ControllerBase
{
    private readonly WorkDistributionService _distribution;
    private readonly WorkIntakeService _intake;
    private readonly WorkConfigService _config;
    private readonly IWorkStore _store;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _time;

    public WorkController(
        WorkDistributionService distribution, WorkIntakeService intake, WorkConfigService config, IWorkStore store,
        ICurrentActor actor, TimeProvider time)
    {
        _distribution = distribution;
        _intake = intake;
        _config = config;
        _store = store;
        _actor = actor;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private async Task<IReadOnlyDictionary<string, WorkQueue>> QueuesAsync(CancellationToken ct) =>
        (await _config.ListQueuesAsync(_actor.TenantId, ct)).ToDictionary(q => q.Id);

    private async Task<WorkItemView> ViewAsync(WorkItem item, CancellationToken ct) =>
        WorkItemView.From(item, await QueuesAsync(ct), Now);

    private ActionResult Outcome(DistributionResult result, WorkItemView? view) => result.Status switch
    {
        DistributionStatus.Ok => Ok(view),
        DistributionStatus.NotFound => NotFound(new { error = result.Error }),
        DistributionStatus.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
        _ => Conflict(new { error = result.Error }),
    };

    /// <summary>
    /// Get Next: the caller's own active lease if they hold one, else the
    /// highest-priority eligible item of their queues (primary, then
    /// overflow), leased to them. 204 when there is nothing to work.
    /// </summary>
    [HttpPost("next")]
    [RequirePermission(ChoRolePermissions.WorkQueueWork)]
    [ProducesResponseType(typeof(GetNextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> GetNext([FromQuery] WorkType? workType, CancellationToken ct)
    {
        var result = await _distribution.GetNextAsync(_actor, workType, ct);
        if (result?.Item is null) return NoContent();
        var view = await ViewAsync(result.Item, ct);
        return Ok(new GetNextResponse { Item = view, Resumed = result.Resumed, OpenUrl = view.OpenUrl });
    }

    /// <summary>The caller's leased items, highest priority first.</summary>
    [HttpGet("mine")]
    [RequirePermission(ChoRolePermissions.WorkQueueWork)]
    public async Task<ActionResult<List<WorkItemView>>> Mine(CancellationToken ct)
    {
        var queues = await QueuesAsync(ct);
        var now = Now;
        return Ok((await _distribution.MineAsync(_actor, ct)).Select(i => WorkItemView.From(i, queues, now)).ToList());
    }

    [HttpPost("{id}/release")]
    [RequirePermission(ChoRolePermissions.WorkQueueWork)]
    public async Task<ActionResult> Release(string id, CancellationToken ct)
    {
        var result = await _distribution.ReleaseAsync(_actor, id, ct);
        return Outcome(result, result.Item is null ? null : await ViewAsync(result.Item, ct));
    }

    [HttpPost("{id}/renew")]
    [RequirePermission(ChoRolePermissions.WorkQueueWork)]
    public async Task<ActionResult> Renew(string id, CancellationToken ct)
    {
        var result = await _distribution.RenewAsync(_actor, id, ct);
        return Outcome(result, result.Item is null ? null : await ViewAsync(result.Item, ct));
    }

    /// <summary>
    /// Raises one piece of work a person asked for: a follow-up from a
    /// contact. Only work types no source service owns
    /// (<see cref="WorkTypes.IsPersonCreated"/>) can be created this way, and
    /// only by someone the type's own entitlement permits — the same
    /// permission its domain service enforces on the action the work leads to.
    /// </summary>
    [HttpPost]
    [RequirePermission(ChoRolePermissions.WorkQueueWork)]
    [ProducesResponseType(typeof(WorkItemView), StatusCodes.Status201Created)]
    public async Task<ActionResult<WorkItemView>> Create([FromBody] CreateWorkRequest request, CancellationToken ct)
    {
        if (!WorkTypes.IsPersonCreated(request.WorkType))
            return BadRequest(new { error = $"{request.WorkType} work comes from its source service, not from a person." });
        if (!WorkEntitlements.Permits(_actor, request.WorkType))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"You may not raise {request.WorkType} work." });

        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return BadRequest(new { error = "title is required." });
        if (title.Length > 500) return BadRequest(new { error = "title is at most 500 characters." });
        if (request.OpenUrl is { } url && !WorkEntitlements.IsSafePortalPath(url))
            return BadRequest(new { error = "openUrl must be a relative portal path, such as /calls/abc." });
        // A deadline already past would be born overdue and jump every real
        // deadline in the queue; an absurd one would never surface at all.
        if (request.DueAt is { } due && (due < Now.AddDays(-1) || due > Now.AddYears(2)))
            return BadRequest(new { error = "dueAt must be within the next two years and not already past." });

        var item = await _intake.CreateAdHocAsync(new WorkIntakeService.AdHocWork(
            _actor.TenantId, request.WorkType, request.SourceNumber?.Trim() ?? string.Empty, title, request.OpenUrl,
            request.ReasonCodes ?? [], request.Facets ?? new(), request.MemberId?.Trim(), request.ProviderNpi?.Trim(),
            request.LineOfBusiness?.Trim(), request.Expedited, request.DueAt, _actor.UserId), ct);

        var view = await ViewAsync(item, ct);
        return CreatedAtAction(nameof(Get), new { id = item.Id }, view);
    }

    [HttpPost("from-contact/{contactId}/{key}")]
    [RequirePermission(ChoRolePermissions.WorkQueueWork)]
    public async Task<ActionResult<WorkItemView>> FromContact(string contactId, string key,
        [FromServices] CloudHealthOffice.Infrastructure.Contacts.IContactIntakeReader contacts, CancellationToken ct)
    {
        if (!WorkEntitlements.Permits(_actor, WorkType.Inquiry)) return Forbid();
        CloudHealthOffice.Infrastructure.Contacts.ContactIntakeReceipt? contact;
        try { contact = await contacts.GetAsync(contactId, ct); }
        catch (HttpRequestException) { return StatusCode(503, "Contact receipt is unavailable."); }
        if (contact is null || contact.Id != contactId || contact.TenantId != _actor.TenantId) return NotFound();
        var f = contact.FollowUps.SingleOrDefault(f => f.Key == key);
        if (f is null) return BadRequest("No saved follow-up intent has this key.");
        var source = CloudHealthOffice.Infrastructure.Contacts.ContactIntakeIdentity.For(_actor.TenantId, contactId + ":" + key);
        var item = await _intake.CreateAdHocAsync(new WorkIntakeService.AdHocWork(
            _actor.TenantId, WorkType.Inquiry, contact.Id, f.Title, "/calls/" + Uri.EscapeDataString(contact.Id),
            [], new Dictionary<string, List<string>> { ["channel"] = [f.Channel],
                ["caller"] = [f.CallerType], ["classification"] = [f.Classification] },
            f.MemberId, null, null, false, f.DueAt, f.RequestedBy, source, f.RequestedAt), ct);
        return Ok(await ViewAsync(item, ct));
    }

    /// <summary>
    /// The holder marks person-created work done. Source-owned work (a claim,
    /// an enrollment exception) is completed by its source, and this refuses
    /// it with 409 rather than letting the queue disagree with the record.
    /// </summary>
    [HttpPost("{id}/complete")]
    [RequirePermission(ChoRolePermissions.WorkQueueWork)]
    public async Task<ActionResult> Complete(string id, [FromBody] CompleteRequest? request, CancellationToken ct)
    {
        var result = await _distribution.CompleteAsync(_actor, id, request?.Resolution, ct);
        return Outcome(result, result.Item is null ? null : await ViewAsync(result.Item, ct));
    }

    /// <summary>Supervisor list: filtered, highest priority first, paged. Active items by default.</summary>
    [HttpGet]
    [RequirePermission(ChoRolePermissions.WorkQueueAssign)]
    public async Task<ActionResult<WorkItemPage>> List(
        [FromQuery] string? queueId, [FromQuery] WorkItemStatus? status, [FromQuery] string? assignedTo,
        [FromQuery] WorkType? workType, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var size = Math.Clamp(pageSize, 1, 500);
        var (items, total) = await _store.QueryItemsAsync(_actor.TenantId,
            new WorkItemQuery(queueId, status, assignedTo, workType, ActiveOnly: true, Math.Max(1, page), size), ct);
        var queues = await QueuesAsync(ct);
        var now = Now;
        return Ok(new WorkItemPage
        {
            Page = Math.Max(1, page),
            PageSize = size,
            Total = total,
            Items = items.Select(i => WorkItemView.From(i, queues, now)).ToList(),
        });
    }

    /// <summary>Supervisor push: lease the item to a user (assign, or reassign from whoever held it).</summary>
    [HttpPost("{id}/assign")]
    [RequirePermission(ChoRolePermissions.WorkQueueAssign)]
    public async Task<ActionResult> Assign(string id, [FromBody] AssignRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId)) return BadRequest(new { error = "userId is required." });
        var result = await _distribution.AssignAsync(_actor, id, request.UserId.Trim(), ct);
        return Outcome(result, result.Item is null ? null : await ViewAsync(result.Item, ct));
    }

    [HttpPost("{id}/unassign")]
    [RequirePermission(ChoRolePermissions.WorkQueueAssign)]
    public async Task<ActionResult> Unassign(string id, CancellationToken ct)
    {
        var result = await _distribution.UnassignAsync(_actor, id, ct);
        return Outcome(result, result.Item is null ? null : await ViewAsync(result.Item, ct));
    }

    /// <summary>Users a membership names (directly or by team): the reassign picker.</summary>
    [HttpGet("assignees")]
    [RequirePermission(ChoRolePermissions.WorkQueueAssign)]
    public async Task<ActionResult<IReadOnlyList<string>>> Assignees(CancellationToken ct) =>
        Ok(await _config.AssigneesAsync(_actor.TenantId, ct));

    /// <summary>Queues with their open / leased counts.</summary>
    [HttpGet("queues")]
    [RequirePermission(ChoRolePermissions.WorkQueueRead)]
    public async Task<ActionResult<List<QueueSummaryView>>> Queues(CancellationToken ct)
    {
        var counts = (await _store.CountByQueueAsync(_actor.TenantId, ct)).ToDictionary(c => c.QueueId);
        return Ok((await _config.ListQueuesAsync(_actor.TenantId, ct)).Select(q => new QueueSummaryView
        {
            Id = q.Id,
            Name = q.Name,
            WorkType = q.WorkType,
            IsActive = q.IsActive,
            IsFallback = q.IsFallback,
            Order = q.Order,
            SlaDays = q.SlaDays,
            Open = counts.TryGetValue(q.Id, out var c) ? c.Open : 0,
            Leased = counts.TryGetValue(q.Id, out var l) ? l.Leased : 0,
        }).ToList());
    }

    /// <summary>One item with its audit trail.</summary>
    [HttpGet("{id}")]
    [RequirePermission(ChoRolePermissions.WorkQueueRead)]
    public async Task<ActionResult<WorkItemDetail>> Get(string id, CancellationToken ct)
    {
        var item = await _store.GetItemAsync(_actor.TenantId, id, ct);
        if (item is null) return NotFound(new { error = $"Work item {id} not found." });
        return Ok(new WorkItemDetail
        {
            Item = await ViewAsync(item, ct),
            Audit = (await _store.ListAuditAsync(_actor.TenantId, id, ct)).ToList(),
        });
    }
}
