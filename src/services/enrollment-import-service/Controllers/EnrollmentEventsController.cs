using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace EnrollmentImportService.Controllers;

/// <summary>
/// Read-side API for the per-member enrollment-event stream.
/// </summary>
[ApiController]
[Route("api/v1/members")]
public class EnrollmentEventsController : ControllerBase
{
    private readonly IEnrollmentEventRepository _repository;

    public EnrollmentEventsController(IEnrollmentEventRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// List enrollment events for a member, newest first. Optionally filter by
    /// <paramref name="type"/> (enum name or numeric value) and an occurredAt window.
    /// Returns a continuation token for paging through large histories.
    /// </summary>
    [HttpGet("{memberId}/enrollment-events")]
    [ProducesResponseType(typeof(EnrollmentEventListResponse), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> List(
        string memberId,
        [FromHeader(Name = "X-Tenant-ID")] string tenantId,
        [FromQuery] string? type = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? continuationToken = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { error = "X-Tenant-ID header is required" });
        if (string.IsNullOrWhiteSpace(memberId))
            return BadRequest(new { error = "memberId is required" });

        EnrollmentEventType? typeFilter = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<EnrollmentEventType>(type, ignoreCase: true, out var parsed))
                return BadRequest(new { error = $"Unknown event type '{type}'" });
            typeFilter = parsed;
        }

        var query = new EnrollmentEventQuery(
            EventType: typeFilter,
            FromUtc: from,
            ToUtc: to,
            Limit: Math.Clamp(limit, 1, 200),
            ContinuationToken: continuationToken);

        var page = await _repository.ListByMemberAsync(tenantId, memberId, query, ct);
        return Ok(new EnrollmentEventListResponse(page.Items, page.ContinuationToken));
    }
}

public sealed record EnrollmentEventListResponse(
    IReadOnlyList<EnrollmentEvent> Items,
    string? ContinuationToken);
