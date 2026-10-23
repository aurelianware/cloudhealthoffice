using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.AspNetCore.Mvc;
using TenantService.Hierarchy;

namespace TenantService.Controllers;

/// <summary>
/// Carrier records: the tenant's legal entities (plan-configuration.md §3).
/// Reading needs <c>config:read</c> (services' own tokens satisfy it);
/// changing one needs <c>config:enterprise</c>, as a tenant- or carrier-level
/// setting does (§9a). The tenant is always the caller's, from its token.
///
/// <para>
/// A tenant that has no carrier gets its default one on the first read, built
/// from its own record, so callers never have to handle "this tenant has no
/// carrier yet".
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/carriers")]
[RequirePermission(PlanConfigurationPermissions.Read)]
public sealed class CarriersController(HierarchyService hierarchy, ICurrentActor actor) : ControllerBase
{
    /// <summary>The tenant's carriers, the default one included.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<Carrier>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await hierarchy.ListCarriersAsync(actor.TenantId, ct));

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Carrier), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct) =>
        await hierarchy.GetCarrierAsync(actor.TenantId, id, ct) is { } carrier
            ? Ok(carrier)
            : NotFound(new { error = $"No carrier '{id}'." });

    /// <summary>Adds a carrier, for an organization that runs more than one legal entity.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(Carrier), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CarrierWrite write, CancellationToken ct)
    {
        var result = await hierarchy.CreateCarrierAsync(actor, write ?? new CarrierWrite(), ct);
        return result.Outcome == HierarchyOutcome.Ok
            ? CreatedAtAction(nameof(Get), new { id = result.Value!.Id }, result.Value)
            : Respond(result);
    }

    /// <summary>Changes a carrier's details. Its code is fixed; create another instead.</summary>
    [HttpPatch("{id}")]
    [ProducesResponseType(typeof(Carrier), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromRoute] string id, [FromBody] CarrierWrite write, CancellationToken ct) =>
        Respond(await hierarchy.UpdateCarrierAsync(actor, id, write ?? new CarrierWrite(), ct));

    private IActionResult Respond<T>(HierarchyResult<T> result) => result.Outcome switch
    {
        HierarchyOutcome.Ok => Ok(result.Value),
        HierarchyOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { errors = result.Errors }),
        HierarchyOutcome.NotFound => NotFound(new { errors = result.Errors }),
        HierarchyOutcome.Conflict => Conflict(new { errors = result.Errors }),
        _ => UnprocessableEntity(new { errors = result.Errors }),
    };
}
