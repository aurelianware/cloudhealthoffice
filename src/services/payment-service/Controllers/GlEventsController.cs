using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Services;

namespace PaymentService.Controllers;

/// <summary>
/// The GL source events of the token's tenant that have not been delivered to the
/// GL yet (or keep failing): type, event id, where it lives, attempts, last error.
/// Payloads are totals and identifiers only. payments:read.
/// </summary>
[ApiController]
[Route("api/gl-events")]
[Produces("application/json")]
public sealed class GlEventsController : ControllerBase
{
    private readonly ICurrentActor _actor;

    public GlEventsController(ICurrentActor actor) => _actor = actor;

    [HttpGet("unpublished")]
    [ProducesResponseType(typeof(IEnumerable<UnpublishedGlEventView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<ActionResult<IEnumerable<UnpublishedGlEventView>>> Unpublished(
        [FromServices] IServiceProvider services, CancellationToken cancellationToken)
    {
        var store = services.GetService<IGlOutboxStore>();
        if (store == null)
            return Problem(title: "Not available", detail: "GL event outboxes are read from MongoDB only.",
                statusCode: StatusCodes.Status501NotImplemented);
        var pending = await store.ListUnpublishedAsync(_actor.TenantId, cancellationToken);
        return Ok(pending.Select(p => new UnpublishedGlEventView
        {
            EventId = p.Message.EventId,
            Type = p.Message.Type,
            Source = p.Source.ToString(),
            DocumentId = p.DocumentId,
            CreatedAt = p.Message.CreatedAt,
            PublishAttempts = p.Message.PublishAttempts,
            LastError = p.Message.LastError,
            NextAttemptAt = p.Message.NextAttemptAt,
        }));
    }
}

public sealed class UnpublishedGlEventView
{
    public string EventId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string DocumentId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public int PublishAttempts { get; init; }
    public string? LastError { get; init; }
    public DateTime? NextAttemptAt { get; init; }
}
