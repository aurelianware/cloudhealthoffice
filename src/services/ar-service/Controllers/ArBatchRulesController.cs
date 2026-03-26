using Microsoft.AspNetCore.Mvc;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Controllers;

[ApiController]
[Route("api/v1/ar/batch-rules")]
[Produces("application/json")]
public class ArBatchRulesController : ControllerBase
{
    private readonly IArBatchRuleRepository _batchRuleRepository;
    private readonly ILogger<ArBatchRulesController> _logger;

    public ArBatchRulesController(
        IArBatchRuleRepository batchRuleRepository,
        ILogger<ArBatchRulesController> logger)
    {
        _batchRuleRepository = batchRuleRepository;
        _logger = logger;
    }

    /// <summary>
    /// Search batch rules with optional filters
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ArBatchRule>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ArBatchRule>>> SearchBatchRules(
        [FromQuery] BatchRuleTrigger? trigger = null,
        [FromQuery] BatchRuleStatus? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var results = await _batchRuleRepository.SearchAsync(trigger, status, page, pageSize);
        return Ok(results);
    }

    /// <summary>
    /// Get batch rule by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ArBatchRule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ArBatchRule>> GetBatchRuleById(string id)
    {
        var rule = await _batchRuleRepository.GetByIdAsync(id);
        if (rule == null)
            return NotFound(new { error = $"Batch rule {id} not found" });
        return Ok(rule);
    }

    /// <summary>
    /// Create a new batch rule
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ArBatchRule), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArBatchRule>> CreateBatchRule([FromBody] ArBatchRule rule)
    {
        _logger.LogInformation("Creating batch rule {RuleCode} ({RuleName}), trigger={Trigger}",
            SanitizeForLog(rule.RuleCode), SanitizeForLog(rule.RuleName), rule.Trigger);

        var created = await _batchRuleRepository.CreateAsync(rule);
        return CreatedAtAction(nameof(GetBatchRuleById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update a batch rule
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(ArBatchRule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ArBatchRule>> UpdateBatchRule(string id, [FromBody] ArBatchRule rule)
    {
        var existing = await _batchRuleRepository.GetByIdAsync(id);
        if (existing == null)
            return NotFound(new { error = $"Batch rule {id} not found" });

        rule.Id = id;
        rule.TenantId = existing.TenantId;
        rule.CreatedAt = existing.CreatedAt;
        rule.CreatedBy = existing.CreatedBy;

        var updated = await _batchRuleRepository.UpdateAsync(rule);
        return Ok(updated);
    }

    /// <summary>
    /// Dry-run test of a batch rule — returns projected debit/credit amounts for a sample amount
    /// </summary>
    [HttpPost("{id}/test")]
    [ProducesResponseType(typeof(TestBatchRuleResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TestBatchRuleResult>> TestBatchRule(string id, [FromBody] TestBatchRuleRequest request)
    {
        var rule = await _batchRuleRepository.GetByIdAsync(id);
        if (rule == null)
            return NotFound(new { error = $"Batch rule {id} not found" });

        var debitAmount = request.SampleAmount;
        var creditAmount = request.SampleAmount;

        // If split behavior is configured, apply the split logic
        if (rule.SplitBehavior == BatchRuleSplitBehavior.SplitByAccountConfig)
        {
            // In a real scenario, we'd look up the GL account's PremiumSplitConfig.
            // For dry-run, return the full amount on both sides (balanced entry).
            debitAmount = request.SampleAmount;
            creditAmount = request.SampleAmount;
        }

        var result = new TestBatchRuleResult
        {
            RuleId = rule.Id,
            RuleCode = rule.RuleCode,
            Trigger = rule.Trigger,
            SampleAmount = request.SampleAmount,
            ProjectedDebitAmount = debitAmount,
            ProjectedCreditAmount = creditAmount,
            DebitAccountId = rule.DebitAccountId,
            CreditAccountId = rule.CreditAccountId,
            SplitBehavior = rule.SplitBehavior,
            AutoApproved = rule.AutoApproveThreshold.HasValue && request.SampleAmount <= rule.AutoApproveThreshold.Value
        };

        _logger.LogInformation("Dry-run test of batch rule {RuleCode} with sample amount {Amount}",
            rule.RuleCode, request.SampleAmount);

        return Ok(result);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class TestBatchRuleRequest
{
    public decimal SampleAmount { get; set; }
}

public class TestBatchRuleResult
{
    public string RuleId { get; set; } = string.Empty;
    public string RuleCode { get; set; } = string.Empty;
    public BatchRuleTrigger Trigger { get; set; }
    public decimal SampleAmount { get; set; }
    public decimal ProjectedDebitAmount { get; set; }
    public decimal ProjectedCreditAmount { get; set; }
    public string DebitAccountId { get; set; } = string.Empty;
    public string CreditAccountId { get; set; } = string.Empty;
    public BatchRuleSplitBehavior SplitBehavior { get; set; }
    public bool AutoApproved { get; set; }
}
