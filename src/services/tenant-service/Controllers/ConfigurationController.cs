using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.AspNetCore.Mvc;
using TenantService.Configuration;
using TenantService.Hierarchy;

namespace TenantService.Controllers;

/// <summary>
/// Plan configuration (docs/architecture/plan-configuration.md): the setting
/// catalog, resolution with its explanation, overrides, history, changes and
/// two-person approval. The tenant is always the caller's, from its token.
/// Reading needs <c>config:read</c> (services' own tokens satisfy it). A
/// write needs the permission for its level as well (§9a):
/// <c>config:sponsor</c> at sponsor level, <c>config:enterprise</c> at line
/// of business and tenant level; approving needs <c>config:approve</c>.
/// Nodes in routes: <c>tenant/_</c>, <c>carrier/{carrierId}</c>,
/// <c>lineOfBusiness/Commercial</c>, <c>program/{programId}</c>,
/// <c>benefitPlan/{planId}</c>, <c>sponsor/{groupNumber}</c>.
/// </summary>
[ApiController]
[Route("api/v1/configuration")]
[RequirePermission(PlanConfigurationPermissions.Read)]
public sealed class ConfigurationController(
    ConfigurationService configuration, ISettingCatalog catalog, ICurrentActor actor, HierarchyService hierarchy) : ControllerBase
{
    /// <summary>Every defined setting: type, defaults, allowed levels, constraints, change control.</summary>
    [HttpGet("catalog")]
    public IActionResult Catalog() => Ok(catalog.All.OrderBy(d => d.Key));

    /// <summary>
    /// A setting's value for a member's position on a date, where it came from,
    /// and every level checked. Naming a <c>program</c> is enough to place the
    /// member: its carrier and line of business are read from the program
    /// record, so a caller holding one id gets the whole branch above it
    /// (plan-configuration.md §7).
    /// </summary>
    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve([FromQuery] string key, [FromQuery] DateTime? asOf,
        [FromQuery] string? lineOfBusiness, [FromQuery] string? sponsor, [FromQuery] string? carrier,
        [FromQuery] string? program, [FromQuery] string? benefitPlan, CancellationToken ct)
    {
        if (lineOfBusiness is not null && LinesOfBusiness.Normalize(lineOfBusiness) is null) return UnknownLine(lineOfBusiness);
        var scope = await CompleteAsync(new SettingScope
        {
            LineOfBusiness = lineOfBusiness, Sponsor = sponsor, Carrier = carrier, Program = program, BenefitPlan = benefitPlan,
        }, ct);
        var resolved = await configuration.ResolveAsync(actor.TenantId, key, scope, asOf, ct);
        return resolved is null ? UnknownKey(key) : Ok(resolved);
    }

    public sealed record ResolveRequest(string Key, DateTime? AsOf, string? LineOfBusiness, string? Sponsor,
        string? Carrier = null, string? Program = null, string? BenefitPlan = null);

    /// <summary>Several resolutions at once (up to 100).</summary>
    [HttpPost("resolve")]
    public async Task<IActionResult> ResolveMany([FromBody] List<ResolveRequest> requests, CancellationToken ct)
    {
        if (requests is null || requests.Count is 0 or > 100) return UnprocessableEntity(new { error = "Send between 1 and 100 requests." });
        var results = new List<ResolvedSetting>(requests.Count);
        foreach (var r in requests)
        {
            if (r.LineOfBusiness is not null && LinesOfBusiness.Normalize(r.LineOfBusiness) is null) return UnknownLine(r.LineOfBusiness);
            var scope = await CompleteAsync(new SettingScope
            {
                LineOfBusiness = r.LineOfBusiness, Sponsor = r.Sponsor, Carrier = r.Carrier, Program = r.Program, BenefitPlan = r.BenefitPlan,
            }, ct);
            var resolved = await configuration.ResolveAsync(actor.TenantId, r.Key, scope, r.AsOf, ct);
            if (resolved is null) return UnknownKey(r.Key);
            results.Add(resolved);
        }
        return Ok(results);
    }

    /// <summary>
    /// Fills in the levels above a named program. A program the tenant does not
    /// have is left as the caller gave it: the resolution then simply finds
    /// nothing at that node, which is the honest answer, rather than failing a
    /// read of an unrelated setting.
    /// </summary>
    private async Task<SettingScope> CompleteAsync(SettingScope scope, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(scope.Program)
        || await hierarchy.GetProgramAsync(actor.TenantId, scope.Program.Trim(), ct) is not { } program
            ? scope
            : scope.CompletedFrom(program.ToDescriptor());

    /// <summary>Every override of a key at a node's level and every more specific level ("where is this overridden?").</summary>
    [HttpGet("overrides")]
    public async Task<IActionResult> Overrides([FromQuery] string key, [FromQuery] string level = "tenant", [FromQuery] string? scopeId = null,
        [FromQuery] DateTime? asOf = null, CancellationToken ct = default)
    {
        if (ParseLevel(level) is not { } parsed) return UnknownLevel(level);
        var overrides = await configuration.OverridesAsync(actor.TenantId, key, parsed, scopeId, asOf, ct);
        return overrides is null ? UnknownKey(key) : Ok(overrides);
    }

    /// <summary>Every version stored at one node.</summary>
    [HttpGet("values/{level}/{scopeId}/{key}/history")]
    public async Task<IActionResult> History([FromRoute] string level, [FromRoute] string scopeId, [FromRoute] string key, CancellationToken ct)
    {
        if (ParseLevel(level) is not { } parsed) return UnknownLevel(level);
        var history = await configuration.HistoryAsync(actor.TenantId, key, parsed, scopeId, ct);
        return history is null ? UnknownKey(key) : Ok(history);
    }

    /// <summary>Sets a value at a node from a date (two-person settings wait for approval: 202).</summary>
    [HttpPut("values/{level}/{scopeId}/{key}")]
    public async Task<IActionResult> Put([FromRoute] string level, [FromRoute] string scopeId, [FromRoute] string key,
        [FromBody] SettingWrite write, CancellationToken ct)
    {
        if (ParseLevel(level) is not { } parsed) return UnknownLevel(level);
        return Respond(await configuration.WriteAsync(actor, key, parsed, scopeId, write ?? new SettingWrite(), clear: false, ct));
    }

    /// <summary>Stops a node setting a value from a date; the next level up applies again. History is kept.</summary>
    [HttpDelete("values/{level}/{scopeId}/{key}")]
    public async Task<IActionResult> Clear([FromRoute] string level, [FromRoute] string scopeId, [FromRoute] string key,
        [FromQuery] DateTime? effectiveFrom, [FromQuery] string? reason, CancellationToken ct)
    {
        if (ParseLevel(level) is not { } parsed) return UnknownLevel(level);
        return Respond(await configuration.WriteAsync(actor, key, parsed, scopeId,
            new SettingWrite { EffectiveFrom = effectiveFrom, Reason = reason }, clear: true, ct));
    }

    [HttpGet("pending")]
    public async Task<IActionResult> Pending(CancellationToken ct) => Ok(await configuration.PendingAsync(actor.TenantId, ct));

    public sealed record DecisionRequest(string? Note);

    [HttpPost("pending/{id}/approve")]
    public async Task<IActionResult> Approve([FromRoute] string id, [FromBody] DecisionRequest? body, CancellationToken ct) =>
        Respond(await configuration.ApproveAsync(actor, id, body?.Note, ct));

    [HttpPost("pending/{id}/reject")]
    public async Task<IActionResult> Reject([FromRoute] string id, [FromBody] DecisionRequest? body, CancellationToken ct) =>
        Respond(await configuration.RejectAsync(actor, id, body?.Note, ct));

    /// <summary>The change log: who changed what, when, why, before and after.</summary>
    [HttpGet("changes")]
    public async Task<IActionResult> Changes([FromQuery] string? key, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        Ok(await configuration.ChangesAsync(actor.TenantId, key, limit, ct));

    private IActionResult Respond(ConfigurationResult result) => result.Outcome switch
    {
        ConfigurationOutcome.Applied when result.Value is not null => Ok(result.Value),
        ConfigurationOutcome.Applied => Ok(result.Pending),
        ConfigurationOutcome.Pending => Accepted(result.Pending),
        ConfigurationOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { errors = result.Errors }),
        ConfigurationOutcome.NotFound => NotFound(new { errors = result.Errors }),
        ConfigurationOutcome.Conflict => Conflict(new { errors = result.Errors }),
        _ => UnprocessableEntity(new { errors = result.Errors }),
    };

    /// <summary>Route names for the levels that hold values today (others are refused by the setting's allowed levels).</summary>
    public static SettingLevel? ParseLevel(string level) => level.Trim().ToLowerInvariant() switch
    {
        "tenant" => SettingLevel.Tenant,
        "lineofbusiness" or "line-of-business" or "lob" => SettingLevel.LineOfBusiness,
        "sponsor" => SettingLevel.Sponsor,
        "carrier" => SettingLevel.Carrier,
        "program" => SettingLevel.Program,
        "benefitplan" or "benefit-plan" or "plan" => SettingLevel.BenefitPlan,
        "sponsorpolicy" or "sponsor-policy" or "policy" => SettingLevel.SponsorPolicy,
        "class" => SettingLevel.Class,
        _ => null,
    };

    private UnprocessableEntityObjectResult UnknownLevel(string level) =>
        UnprocessableEntity(new { error = $"Unknown level '{level}'. Use tenant, carrier, lineOfBusiness, program, benefitPlan or sponsor." });

    private NotFoundObjectResult UnknownKey(string key) => NotFound(new { error = $"Setting '{key}' is not defined." });

    private UnprocessableEntityObjectResult UnknownLine(string value) =>
        UnprocessableEntity(new { error = $"'{value}' is not a line of business. Use one of {string.Join(", ", LinesOfBusiness.All)}." });
}
