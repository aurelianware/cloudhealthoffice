using EnrollmentImportService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace EnrollmentImportService.Controllers;

/// <summary>
/// The rate cell inputs each member's 834 transactions carried, for
/// building expected capitation member-months
/// (docs/integration/MEDICAID-EXPECTED-MEMBER-MONTHS.md). Read with
/// <c>enrollment:read</c>; premium-billing-service reads it with its own
/// service identity.
/// </summary>
[ApiController]
[Route("api/v1/enrollment/rate-cell-inputs")]
public sealed class RateCellInputsController : ControllerBase
{
    /// <summary>Most members in one page.</summary>
    public const int MaxMembers = 5000;

    private readonly IEnrollmentEventRepository _repository;
    private readonly ICurrentActor _actor;

    public RateCellInputsController(IEnrollmentEventRepository repository, ICurrentActor actor)
    {
        _repository = repository;
        _actor = actor;
    }

    /// <summary>
    /// Members (ordinal by member id, after <paramref name="after"/>) with
    /// every event that carried rate cell inputs, oldest first. Page on with
    /// the returned <c>next</c> until it is null.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(RateCellInputsPage), 200)]
    public async Task<IActionResult> List([FromQuery] string? after = null, [FromQuery] int limit = 1000, CancellationToken ct = default)
    {
        var size = Math.Clamp(limit, 1, MaxMembers);
        var members = await _repository.ListRateCellInputsAsync(_actor.TenantId, after, size, ct);
        return Ok(new RateCellInputsPage(members, members.Count == size ? members[^1].MemberId : null));
    }
}

public sealed record RateCellInputsPage(IReadOnlyList<MemberRateCellHistory> Members, string? Next);
