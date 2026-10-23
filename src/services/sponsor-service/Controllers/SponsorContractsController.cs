using System;
using System.Threading;
using System.Threading.Tasks;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using SponsorService.Contracts;

namespace SponsorService.Controllers;

/// <summary>
/// Sponsor policies and contract terms
/// (docs/architecture/plan-configuration.md §9b). The sponsor's group number
/// identifies the account and never changes, so everything here hangs off it;
/// the tenant comes from the caller's token.
///
/// <para>
/// Reading needs <c>config:read</c> or <c>enrollment:read</c> — enrollment
/// has to see which policies a group offers. Writing needs
/// <c>config:sponsor</c>, which is the permission §9a already assigns to
/// sponsor-level configuration and the one the account manager who enters a
/// contract holds; it is the same choice the carrier and program records made
/// in step 2, where each write needs the permission its level needs for a
/// setting.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/sponsors/{groupNumber}")]
public sealed class SponsorContractsController(ContractService contracts, ICurrentActor actor) : ControllerBase
{
    private string TenantId => actor.TenantId;

    /// <summary>Every policy the group offers, by policy number.</summary>
    [HttpGet("policies")]
    [RequirePermission("config:read,enrollment:read")]
    public async Task<IActionResult> ListPolicies([FromRoute] string groupNumber, CancellationToken ct) =>
        Respond(await contracts.ListPoliciesAsync(TenantId, groupNumber, ct));

    [HttpGet("policies/{policyNumber}")]
    [RequirePermission("config:read,enrollment:read")]
    public async Task<IActionResult> GetPolicy([FromRoute] string groupNumber, [FromRoute] string policyNumber, CancellationToken ct) =>
        Respond(await contracts.GetPolicyAsync(TenantId, groupNumber, policyNumber, ct));

    /// <summary>Adds a policy: a benefit plan offered under its own policy (sub-group) number.</summary>
    [HttpPost("policies")]
    [RequirePermission("config:sponsor")]
    public async Task<IActionResult> CreatePolicy([FromRoute] string groupNumber, [FromBody] PolicyWrite write, CancellationToken ct)
    {
        var result = await contracts.CreatePolicyAsync(actor, groupNumber, write ?? new PolicyWrite(), ct);
        return result.Outcome == ContractOutcome.Ok
            ? Created($"api/v1/sponsors/{groupNumber}/policies/{result.Value!.PolicyNumber}", result.Value)
            : Respond(result);
    }

    /// <summary>
    /// Changes a policy's name, dates, classes or whether it is offered. The
    /// policy number, benefit plan and program are refused rather than
    /// ignored: changing one of those is a new policy with the old one ended,
    /// so that members move through the renewal's enrollment (§9b).
    /// </summary>
    [HttpPatch("policies/{policyNumber}")]
    [RequirePermission("config:sponsor")]
    public async Task<IActionResult> UpdatePolicy([FromRoute] string groupNumber, [FromRoute] string policyNumber,
        [FromBody] PolicyWrite write, CancellationToken ct) =>
        Respond(await contracts.UpdatePolicyAsync(actor, groupNumber, policyNumber, write ?? new PolicyWrite(), ct));

    /// <summary>Every contract term the group has had, oldest first.</summary>
    [HttpGet("contract-terms")]
    [RequirePermission("config:read,enrollment:read")]
    public async Task<IActionResult> ListTerms([FromRoute] string groupNumber, CancellationToken ct) =>
        Respond(await contracts.ListTermsAsync(TenantId, groupNumber, ct));

    /// <summary>
    /// The term to resolve a date against: the one signed or active whose
    /// period covers it. 404 means the group has no such term, which is every
    /// group until one is entered — a consumer falls back on what it reads
    /// today rather than treating it as a failure.
    /// </summary>
    [HttpGet("contract-terms/in-force")]
    [RequirePermission("config:read,enrollment:read")]
    public async Task<IActionResult> TermInForce([FromRoute] string groupNumber, [FromQuery] DateTime? asOf, CancellationToken ct)
    {
        var on = asOf ?? DateTime.UtcNow;
        var term = await contracts.TermInForceAsync(TenantId, groupNumber, on, ct);
        return term is null
            ? NotFound(new { error = $"Group '{groupNumber}' has no contract term in force on {on:yyyy-MM-dd}." })
            : Ok(term);
    }

    /// <summary>Opens a draft term for a contract period.</summary>
    [HttpPost("contract-terms")]
    [RequirePermission("config:sponsor")]
    public async Task<IActionResult> CreateTerm([FromRoute] string groupNumber, [FromBody] TermWrite write, CancellationToken ct)
    {
        var result = await contracts.CreateTermAsync(actor, groupNumber, write ?? new TermWrite(), ct);
        return result.Outcome == ContractOutcome.Ok
            ? Created($"api/v1/sponsors/{groupNumber}/contract-terms/{result.Value!.PeriodStart:yyyy-MM-dd}", result.Value)
            : Respond(result);
    }

    /// <summary>
    /// Moves a term along §9b's flow: draft → underwriting → signed → active,
    /// or ended. Signing takes the id the signed agreement was stored under.
    /// </summary>
    [HttpPut("contract-terms/{periodStart}/status")]
    [RequirePermission("config:sponsor")]
    public async Task<IActionResult> SetTermStatus([FromRoute] string groupNumber, [FromRoute] DateTime periodStart,
        [FromBody] TermStatusWrite write, CancellationToken ct)
    {
        if (write is null) return UnprocessableEntity(new { error = "A status is required." });
        return Respond(await contracts.SetTermStatusAsync(actor, groupNumber, periodStart, write, ct));
    }

    private IActionResult Respond<T>(ContractResult<T> result) => result.Outcome switch
    {
        ContractOutcome.Ok => Ok(result.Value),
        ContractOutcome.NotFound => NotFound(new { errors = result.Errors }),
        ContractOutcome.Conflict => Conflict(new { errors = result.Errors }),
        _ => UnprocessableEntity(new { errors = result.Errors }),
    };
}
