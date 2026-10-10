using System.Text;
using ArService.Gl;
using CloudHealthOffice.Finance.Contracts;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ArService.Controllers;

/// <summary>
/// The general ledger: source events in (payment-service only), the journal, parked
/// events, periods, the ERP extract and the claims payable reconciliation. The tenant
/// comes from the token. Reads need finance:read; GL writes need finance:write from a
/// user (never a service token), and every GL write is refused (409) while
/// GlPosting:Enabled is false. See docs/operations/GL-POSTING-RUNBOOK.md.
/// </summary>
[ApiController]
[Route("api/gl")]
[Produces("application/json")]
public sealed class GlController : ControllerBase
{
    private readonly GlPostingService _posting;
    private readonly IGlJournalRepository _journal;
    private readonly IGlSourceEventRepository _events;
    private readonly IGlPeriodRepository _periods;
    private readonly ICurrentActor _actor;

    public GlController(GlPostingService posting, IGlJournalRepository journal, IGlSourceEventRepository events,
        IGlPeriodRepository periods, ICurrentActor actor)
    {
        _posting = posting;
        _journal = journal;
        _events = events;
        _periods = periods;
        _actor = actor;
    }

    /// <summary>
    /// A GL source event from payment-service's dispatcher (service token of the event's
    /// tenant). 200 with what became of it (posted, parked with the reason, nothing to post),
    /// also for a redelivery. 409 when posting is disabled (nothing stored; the producer retries)
    /// or the event id was seen with another payload.
    /// </summary>
    [HttpPost("events")]
    [RequireServiceClient("payment-service")]
    [ProducesResponseType(typeof(GlIngestResult), StatusCodes.Status200OK)]
    public Task<IActionResult> Ingest([FromBody] GlEventEnvelope envelope, CancellationToken cancellationToken)
        => Run(async () => Ok(await _posting.IngestAsync(envelope, _actor.TenantId, cancellationToken)), userOnly: false);

    [HttpGet("entries")]
    [RequirePermission("finance:read")]
    public async Task<ActionResult<IEnumerable<GlJournalEntry>>> Entries([FromQuery] string? period, CancellationToken cancellationToken)
    {
        if (period != null && !GlPostingService.IsPeriod(period))
            return Problem(title: "Invalid period", detail: "A period is yyyy-MM.", statusCode: StatusCodes.Status400BadRequest);
        return Ok(await _journal.ListAsync(_actor.TenantId, period, cancellationToken));
    }

    [HttpGet("entries/{entryId}")]
    [RequirePermission("finance:read")]
    public async Task<ActionResult<GlJournalEntry>> Entry(string entryId, CancellationToken cancellationToken)
        => await _journal.GetAsync(_actor.TenantId, entryId, cancellationToken) is { } entry ? Ok(entry) : NotFound();

    /// <summary>Posts the mirror image of an entry (an entry is reversed at most once), in an open period.</summary>
    [HttpPost("entries/{entryId}/reverse")]
    [RequirePermission("finance:write")]
    public Task<IActionResult> Reverse(string entryId, [FromBody] GlReverseRequest request, CancellationToken cancellationToken)
        => Run(async () => Ok(await _posting.ReverseAsync(_actor.TenantId, entryId, request.Reason ?? string.Empty, request.EntryDate, _actor.UserId, cancellationToken)));

    [HttpGet("events")]
    [RequirePermission("finance:read")]
    public async Task<ActionResult<IEnumerable<GlSourceEventView>>> Events([FromQuery] GlSourceEventStatus? status, CancellationToken cancellationToken)
        => Ok((await _events.ListAsync(_actor.TenantId, status, cancellationToken)).Select(GlSourceEventView.From));

    /// <summary>Retries a parked event; <c>entryDate</c> (with a reason) moves a closed-period event to a later open date.</summary>
    [HttpPost("events/{eventId}/retry")]
    [RequirePermission("finance:write")]
    public Task<IActionResult> Retry(string eventId, [FromBody] GlRetryRequest? request, CancellationToken cancellationToken)
        => Run(async () => Ok(await _posting.RetryAsync(_actor.TenantId, eventId, request?.EntryDate, request?.Reason, _actor.UserId, cancellationToken)));

    [HttpPost("events/{eventId}/dismiss")]
    [RequirePermission("finance:write")]
    public Task<IActionResult> Dismiss(string eventId, [FromBody] GlReasonRequest request, CancellationToken cancellationToken)
        => Run(async () => Ok(await _posting.DismissAsync(_actor.TenantId, eventId, request.Reason ?? string.Empty, _actor.UserId, cancellationToken)));

    [HttpGet("periods")]
    [RequirePermission("finance:read")]
    public async Task<ActionResult<IEnumerable<GlClosedPeriod>>> Periods(CancellationToken cancellationToken)
        => Ok(await _periods.ListAsync(_actor.TenantId, cancellationToken));

    [HttpPost("periods/{period}/close")]
    [RequirePermission("finance:write")]
    public Task<IActionResult> Close(string period, [FromBody] GlReasonRequest request, CancellationToken cancellationToken)
        => Run(async () => Ok(await _posting.ClosePeriodAsync(_actor.TenantId, period, request.Reason ?? string.Empty, _actor.UserId, cancellationToken)));

    /// <summary>
    /// The period's journal lines for the ERP: <c>format=csv</c> (default; header, rows,
    /// CONTROL trailer) or <c>json</c> (rows and control). Deterministic; the control's
    /// SHA-256 is also returned in <c>X-GL-Extract-Sha256</c>.
    /// </summary>
    [HttpGet("extract")]
    [RequirePermission("finance:read")]
    public async Task<IActionResult> Extract([FromQuery] string? period, [FromQuery] string? format, CancellationToken cancellationToken)
    {
        if (!GlPostingService.IsPeriod(period))
            return Problem(title: "Invalid period", detail: "period=yyyy-MM is required.", statusCode: StatusCodes.Status400BadRequest);
        var extract = GlExtractBuilder.Build(_actor.TenantId, period!, await _journal.ListAsync(_actor.TenantId, period, cancellationToken));
        Response.Headers["X-GL-Extract-Sha256"] = extract.Control.Sha256;
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return Ok(new { control = extract.Control, rows = extract.Rows });
        if (format != null && !string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            return Problem(title: "Invalid format", detail: "format is csv or json.", statusCode: StatusCodes.Status400BadRequest);
        return File(Encoding.UTF8.GetBytes(extract.Csv), "text/csv", $"gl-{_actor.TenantId}-{period}.csv");
    }

    [HttpGet("reconciliation")]
    [RequirePermission("finance:read")]
    public async Task<ActionResult<GlReconciliationReport>> Reconciliation([FromQuery] string? period, CancellationToken cancellationToken)
    {
        if (period != null && !GlPostingService.IsPeriod(period))
            return Problem(title: "Invalid period", detail: "A period is yyyy-MM.", statusCode: StatusCodes.Status400BadRequest);
        var events = await _events.ListAsync(_actor.TenantId, null, cancellationToken);
        var journal = await _journal.ListAsync(_actor.TenantId, null, cancellationToken);
        return Ok(GlReconciliationBuilder.Build(_actor.TenantId, period, events, journal));
    }

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action, bool userOnly = true)
    {
        if (userOnly && _actor.IsService)
            return Problem(title: "User required", detail: "GL corrections are made by a finance user, not a service token.",
                statusCode: StatusCodes.Status403Forbidden);
        try
        {
            return await action();
        }
        catch (GlPostingDisabledException ex)
        {
            return Problem(title: GlPostingDisabledException.Code, detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (GlEventConflictException ex)
        {
            return Problem(title: "Event conflict", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (GlStateException ex)
        {
            return Problem(title: "GL state", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (GlForbiddenException ex)
        {
            return Problem(title: "Forbidden", detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (KeyNotFoundException ex)
        {
            return Problem(title: "Not found", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (ArgumentException ex)
        {
            return Problem(title: "Invalid request", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}

public sealed class GlReverseRequest
{
    public string? Reason { get; set; }
    public DateTime? EntryDate { get; set; }
}

public sealed class GlRetryRequest
{
    public DateTime? EntryDate { get; set; }
    public string? Reason { get; set; }
}

public sealed class GlReasonRequest
{
    public string? Reason { get; set; }
}

/// <summary>A register record without its payload.</summary>
public sealed class GlSourceEventView
{
    public string EventId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public GlSourceEventStatus Status { get; init; }
    public GlParkReason? ParkReason { get; init; }
    public string? ParkDetail { get; init; }
    public string? EntryId { get; init; }
    public string SourceDocumentId { get; init; } = string.Empty;
    public string SourceReference { get; init; } = string.Empty;
    public decimal ClaimedAmount { get; init; }
    public DateTime ReceivedAt { get; init; }
    public int Attempts { get; init; }

    public static GlSourceEventView From(GlSourceEvent e) => new()
    {
        EventId = e.EventId, Type = e.Type, Status = e.Status, ParkReason = e.ParkReason, ParkDetail = e.ParkDetail, EntryId = e.EntryId,
        SourceDocumentId = e.SourceDocumentId, SourceReference = e.SourceReference, ClaimedAmount = e.ClaimedAmount,
        ReceivedAt = e.ReceivedAt, Attempts = e.Attempts,
    };
}
