using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.SeedRules;
using CloudHealthOffice.PriorAuthRuleEngine.Services;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

[ApiController]
[Route("api/v1/prior-auth-rules")]
public sealed class PriorAuthRulesController : ControllerBase
{
    private readonly IPaRuleRepository _repository;
    private readonly ICurrentActor _actor;
    private readonly ILogger<PriorAuthRulesController> _logger;

    public PriorAuthRulesController(
        IPaRuleRepository repository,
        ICurrentActor actor,
        ILogger<PriorAuthRulesController> logger)
    {
        _repository = repository;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>
    /// CMS-0057-F: the list of items and services, excluding drugs, that require
    /// prior authorization for one state, line of business and (optionally)
    /// program, built from the rule set this tenant's requests are evaluated
    /// against. <c>format=csv</c> returns one row per entry for posting as a file.
    /// The list is a draft for the plan's publication review: exemptions,
    /// excluded drug codes and rules it cannot describe are returned so a
    /// reviewer sees everything that shapes it.
    /// </summary>
    [HttpGet("required-services")]
    [RequirePermission("benefits:read")]
    [ProducesResponseType(typeof(PaRequiredServicesList), StatusCodes.Status200OK)]
    public async Task<IActionResult> RequiredServices([FromQuery] string stateCode, [FromQuery] PaLineOfBusiness lob,
        [FromQuery] string? program, [FromQuery] string? format, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stateCode) || stateCode.Trim().Length != 2 || !stateCode.Trim().All(char.IsAsciiLetter))
            return BadRequest(new { error = "Supply a two-letter state code." });
        if (!Enum.IsDefined(lob))
            return BadRequest(new { error = "Supply a line of business." });
        if (program is not null && (program.Trim().Length is 0 or > 32 || !program.Trim().All(char.IsAsciiLetterOrDigit)))
            return BadRequest(new { error = "A program code is letters and digits only." });
        if (format is not (null or "json" or "csv"))
            return BadRequest(new { error = "format is json or csv." });
        var state = stateCode.Trim().ToUpperInvariant();
        program = string.IsNullOrWhiteSpace(program) ? null : program.Trim();

        var (key, rules) = await PaRuleSetResolver.ResolveAsync(_repository, state, lob, program, _actor.TenantId,
            programId: null, ct);
        var list = PaRequiredServicesList.Build(state, lob, program, key, rules, DateTime.UtcNow);
        return format == "csv"
            ? File(System.Text.Encoding.UTF8.GetBytes(list.ToCsv()), "text/csv",
                $"prior-auth-required-services-{state}-{lob}{(program is null ? "" : "-" + program)}.csv".ToLowerInvariant())
            : Ok(list);
    }

    /// <summary>
    /// Idempotently seeds platform prior-auth rules used by local validation.
    ///
    /// Platform rules are shared: every tenant's PA decisions fall back to
    /// them. Writing them is therefore a platform act and requires the
    /// existing <c>platform:admin</c> permission (PlatformAdmin only; the
    /// reserved <c>platform</c> resource is not covered by <c>*:*</c>).
    /// Without it the service default (<c>settings:manage</c>) applied, which
    /// every TenantAdmin holds (T-5).
    /// </summary>
    [HttpPost("seed-platform")]
    [RequirePermission("platform:admin")]
    [ProducesResponseType(typeof(PriorAuthSeedResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PriorAuthSeedResponse>> SeedPlatformRules(CancellationToken ct)
    {
        var platformRules = TxMedicaidSeedRules.GetAll();
        var existingRuleIdsByState = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var rulesToWrite = new List<PaRuleDocument>();

        foreach (var rule in platformRules)
        {
            if (!existingRuleIdsByState.TryGetValue(rule.StateCode, out var existingRuleIds))
            {
                var existing = await _repository.ListAsync(tenantId: null, stateCode: rule.StateCode, ct);
                existingRuleIds = new HashSet<string>(
                    existing
                        .Where(r => r.TenantId is null)
                        .Select(r => r.RuleId),
                    StringComparer.OrdinalIgnoreCase);
                existingRuleIdsByState[rule.StateCode] = existingRuleIds;
            }

            if (existingRuleIds.Add(rule.RuleId))
            {
                rulesToWrite.Add(rule);
            }
        }

        if (rulesToWrite.Count > 0)
        {
            await _repository.BulkUpsertAsync(rulesToWrite, ct);
            _logger.LogInformation("Seeded {Count} platform prior-auth rules", rulesToWrite.Count);
        }

        return Ok(new PriorAuthSeedResponse(platformRules.Count, rulesToWrite.Count));
    }
}

public sealed record PriorAuthSeedResponse(int TotalPlatformRules, int SeededRules);
