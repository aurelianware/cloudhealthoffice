using System;
using System.Threading;
using System.Threading.Tasks;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using SponsorService.Contracts;

namespace SponsorService.Controllers;

/// <summary>
/// One contract year of a policy's terms — rates, contribution, eligibility,
/// waiting period, termination and life events
/// (docs/architecture/plan-configuration.md §9b, step 3b).
///
/// <para>
/// Versions hang off the policy and are named by the contract period's start
/// date, the same date that identifies the contract term, because §9b has the
/// term covering that year's policy terms. Permissions match the policy
/// routes: <c>config:read</c> or <c>enrollment:read</c> to read,
/// <c>config:sponsor</c> to write.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/sponsors/{groupNumber}/policies/{policyNumber}/terms")]
public sealed class PolicyTermsController(PolicyTermsService terms, ICurrentActor actor) : ControllerBase
{
    private string TenantId => actor.TenantId;

    /// <summary>Every contract year entered for this policy, oldest first.</summary>
    [HttpGet]
    [RequirePermission("config:read,enrollment:read")]
    public async Task<IActionResult> List([FromRoute] string groupNumber, [FromRoute] string policyNumber, CancellationToken ct) =>
        Respond(await terms.ListAsync(TenantId, groupNumber, policyNumber, ct));

    /// <summary>One contract year, by the period's start date.</summary>
    [HttpGet("{periodStart}")]
    [RequirePermission("config:read,enrollment:read")]
    public async Task<IActionResult> Get([FromRoute] string groupNumber, [FromRoute] string policyNumber,
        [FromRoute] DateTime periodStart, CancellationToken ct) =>
        Respond(await terms.GetAsync(TenantId, groupNumber, policyNumber, periodStart, ct));

    /// <summary>
    /// The terms to use for a date, found through the contract term in force
    /// over it. Where there are none, the body says which reason applies: the
    /// group has no contract covering the date, the policy is not offered then,
    /// or the contract exists and this policy's terms for it were never
    /// entered. The three are not interchangeable — only the first is a group
    /// that simply has no contract yet, and the last is a contract with a hole
    /// in it that nobody should resolve around.
    /// </summary>
    [HttpGet("in-force")]
    [RequirePermission("config:read,enrollment:read")]
    public async Task<IActionResult> InForce([FromRoute] string groupNumber, [FromRoute] string policyNumber,
        [FromQuery] DateTime? asOf, CancellationToken ct)
    {
        var on = asOf ?? DateTime.UtcNow;
        var resolved = await terms.ResolveAsync(TenantId, groupNumber, policyNumber, on, ct);
        if (resolved.Outcome == TermsResolutionOutcome.Resolved)
            return Ok(new { asOf = on.Date, contractPeriodStart = resolved.ContractTerm!.PeriodStart, terms = resolved.Terms });

        return NotFound(new
        {
            asOf = on.Date,
            reason = resolved.Outcome.ToString(),
            error = Explain(resolved, groupNumber, policyNumber, on),
        });
    }

    /// <summary>
    /// Enters or restates a contract year's terms. The contract term for the
    /// period has to be open first, which is §9b's order of events, and a term
    /// that has been signed is settled: amending it means a new contract
    /// period rather than an edit in place.
    /// </summary>
    [HttpPut("{periodStart}")]
    [RequirePermission("config:sponsor")]
    public async Task<IActionResult> Upsert([FromRoute] string groupNumber, [FromRoute] string policyNumber,
        [FromRoute] DateTime periodStart, [FromBody] PolicyTermsWrite write, CancellationToken ct) =>
        Respond(await terms.UpsertAsync(actor, groupNumber, policyNumber, periodStart, write ?? new PolicyTermsWrite(), ct));

    private static string Explain(TermsResolution resolved, string groupNumber, string policyNumber, DateTime on) =>
        resolved.Outcome switch
        {
            TermsResolutionOutcome.NoContractTerm =>
                $"Group '{groupNumber}' has no contract term signed or active on {on:yyyy-MM-dd}.",
            TermsResolutionOutcome.PolicyNotOffered =>
                $"Policy '{policyNumber}' is not offered on {on:yyyy-MM-dd}.",
            TermsResolutionOutcome.TermsNotEntered =>
                $"Group '{groupNumber}' has a contract term for the period starting {resolved.ContractTerm!.PeriodStart:yyyy-MM-dd}, but policy '{policyNumber}' has no terms entered for it.",
            _ => "No terms in force.",
        };

    private IActionResult Respond<T>(ContractResult<T> result) => result.Outcome switch
    {
        ContractOutcome.Ok => Ok(result.Value),
        ContractOutcome.NotFound => NotFound(new { errors = result.Errors }),
        ContractOutcome.Conflict => Conflict(new { errors = result.Errors }),
        _ => UnprocessableEntity(new { errors = result.Errors }),
    };
}
