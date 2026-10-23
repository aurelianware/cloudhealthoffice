using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.AspNetCore.Mvc;
using TenantService.Hierarchy;

namespace TenantService.Controllers;

/// <summary>
/// Program records: a line of business in a state, under a carrier
/// (plan-configuration.md §3). Reading needs <c>config:read</c>; changing one
/// needs <c>config:product</c>, as a program- or plan-level setting does (§9a).
///
/// <para>
/// <c>GET by-benefit-plan/{planId}</c> is the one other services call: it
/// follows the plan → program link that benefit-plan-service holds, so a
/// caller asks one service one question rather than joining two records. Its
/// 404 means "this plan has no program", and a 503 means "benefit-plan-service
/// could not be asked" — a caller that cannot tell those apart would read a
/// service outage as a member with no program.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/programs")]
[RequirePermission(PlanConfigurationPermissions.Read)]
public sealed class ProgramsController(HierarchyService hierarchy, ICurrentActor actor) : ControllerBase
{
    /// <summary>The tenant's programs, optionally for one carrier.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PlanProgram>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? carrierId, CancellationToken ct) =>
        Ok(await hierarchy.ListProgramsAsync(actor.TenantId, carrierId, ct));

    /// <summary>One program, as other services read it (<see cref="ProgramDescriptor"/> is the subset they need).</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PlanProgram), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct) =>
        await hierarchy.GetProgramAsync(actor.TenantId, id, ct) is { } program
            ? Ok(program)
            : NotFound(new { error = $"No program '{id}'." });

    /// <summary>
    /// The program a benefit plan belongs to: its state, line of business and
    /// program code in one record. 404 when the plan carries no program; 503
    /// when benefit-plan-service could not be reached.
    /// </summary>
    [HttpGet("by-benefit-plan/{planId}")]
    [ProducesResponseType(typeof(PlanProgram), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ForBenefitPlan([FromRoute] string planId, CancellationToken ct)
    {
        var result = await hierarchy.ProgramForBenefitPlanAsync(actor.TenantId, planId, ct);
        return result.Outcome switch
        {
            HierarchyOutcome.Ok => Ok(result.Value),
            HierarchyOutcome.NotFound => NotFound(new { errors = result.Errors }),
            // Not a 404: "could not look" must not read as "there is none".
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, new { errors = result.Errors }),
        };
    }

    /// <summary>Adds a program. With no carrierId it goes under the tenant's default carrier.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(PlanProgram), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] ProgramWrite write, CancellationToken ct)
    {
        var result = await hierarchy.CreateProgramAsync(actor, write ?? new ProgramWrite(), ct);
        return result.Outcome == HierarchyOutcome.Ok
            ? CreatedAtAction(nameof(Get), new { id = result.Value!.Id }, result.Value)
            : Respond(result);
    }

    /// <summary>
    /// Changes a program's name, contract number, dates or status. Its code,
    /// carrier, state and line of business are fixed: everything keyed on the
    /// program would move with them.
    /// </summary>
    [HttpPatch("{id}")]
    [ProducesResponseType(typeof(PlanProgram), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromRoute] string id, [FromBody] ProgramWrite write, CancellationToken ct) =>
        Respond(await hierarchy.UpdateProgramAsync(actor, id, write ?? new ProgramWrite(), ct));

    private IActionResult Respond<T>(HierarchyResult<T> result) => result.Outcome switch
    {
        HierarchyOutcome.Ok => Ok(result.Value),
        HierarchyOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { errors = result.Errors }),
        HierarchyOutcome.NotFound => NotFound(new { errors = result.Errors }),
        HierarchyOutcome.Conflict => Conflict(new { errors = result.Errors }),
        _ => UnprocessableEntity(new { errors = result.Errors }),
    };
}
