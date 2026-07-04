using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.SeedRules;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

[ApiController]
[Route("api/v1/prior-auth-rules")]
public sealed class PriorAuthRulesController : ControllerBase
{
    private readonly IPaRuleRepository _repository;
    private readonly ILogger<PriorAuthRulesController> _logger;

    public PriorAuthRulesController(
        IPaRuleRepository repository,
        ILogger<PriorAuthRulesController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Idempotently seeds platform prior-auth rules used by local validation.
    /// </summary>
    [HttpPost("seed-platform")]
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
