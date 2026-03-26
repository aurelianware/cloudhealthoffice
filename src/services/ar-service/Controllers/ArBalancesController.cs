using Microsoft.AspNetCore.Mvc;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Controllers;

[ApiController]
[Route("api/v1/ar/balances")]
[Produces("application/json")]
public class ArBalancesController : ControllerBase
{
    private readonly IArBalanceRepository _balanceRepository;
    private readonly ILogger<ArBalancesController> _logger;

    public ArBalancesController(
        IArBalanceRepository balanceRepository,
        ILogger<ArBalancesController> logger)
    {
        _balanceRepository = balanceRepository;
        _logger = logger;
    }

    /// <summary>
    /// Search AR balances with optional filters
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ArBalance>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ArBalance>>> SearchBalances(
        [FromQuery] string? accountId = null,
        [FromQuery] DateTime? period = null,
        [FromQuery] bool? isReconciled = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var results = await _balanceRepository.SearchAsync(accountId, period, isReconciled, page, pageSize);
        return Ok(results);
    }

    /// <summary>
    /// Get AR balance by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ArBalance), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ArBalance>> GetBalanceById(string id)
    {
        var balance = await _balanceRepository.GetByIdAsync(id);
        if (balance == null)
            return NotFound(new { error = $"AR balance {id} not found" });
        return Ok(balance);
    }

    /// <summary>
    /// Get all balances for a specific GL account
    /// </summary>
    [HttpGet("account/{accountId}")]
    [ProducesResponseType(typeof(IEnumerable<ArBalance>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ArBalance>>> GetBalancesByAccountId(string accountId)
    {
        var results = await _balanceRepository.GetByAccountIdAsync(accountId);
        return Ok(results);
    }

    /// <summary>
    /// Reconcile an AR balance
    /// </summary>
    [HttpPost("{id}/reconcile")]
    [ProducesResponseType(typeof(ArBalance), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ArBalance>> ReconcileBalance(string id, [FromBody] ReconcileRequest? request = null)
    {
        var balance = await _balanceRepository.GetByIdAsync(id);
        if (balance == null)
            return NotFound(new { error = $"AR balance {id} not found" });

        if (balance.IsReconciled)
            return BadRequest(new { error = "Balance is already reconciled. Un-reconcile first to re-reconcile." });

        // Resolve who reconciled: explicit body → authenticated user → "system"
        var reconciledBy = request?.ReconciledBy;
        if (string.IsNullOrWhiteSpace(reconciledBy))
            reconciledBy = User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(reconciledBy))
            reconciledBy = "system";

        balance.IsReconciled = true;
        balance.ReconciledAt = DateTime.UtcNow;
        balance.ReconciledBy = reconciledBy;
        balance.ReconciliationNotes = request?.Notes;
        _logger.LogInformation("Reconciled AR balance {BalanceId} by {ReconciledBy}",
            SanitizeForLog(id), SanitizeForLog(reconciledBy));

        var updated = await _balanceRepository.UpdateAsync(balance);
        return Ok(updated);
    }

    /// <summary>
    /// Get aggregate aging summary across all balances
    /// </summary>
    [HttpGet("aging")]
    [ProducesResponseType(typeof(AgingSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<AgingSummary>> GetAgingSummary()
    {
        // TODO: Replace with MongoDB $group aggregation pipeline for production scale.
        // Current implementation loads all balance documents into memory —
        // acceptable for dev/demo scale but will degrade at >10k balance records.
        // Tracked for a future refactor to IArBalanceRepository.GetAgingTotalsAsync().
        var allBalances = await _balanceRepository.SearchAsync(page: 1, pageSize: int.MaxValue);

        var summary = new AgingSummary
        {
            Current = allBalances.Sum(b => b.Current),
            Days31To60 = allBalances.Sum(b => b.Days31To60),
            Days61To90 = allBalances.Sum(b => b.Days61To90),
            Days91To120 = allBalances.Sum(b => b.Days91To120),
            Over120Days = allBalances.Sum(b => b.Over120Days)
        };
        summary.Total = summary.Current + summary.Days31To60 + summary.Days61To90
            + summary.Days91To120 + summary.Over120Days;

        return Ok(summary);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class ReconcileRequest
{
    public string ReconciledBy { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class AgingSummary
{
    public decimal Current { get; set; }
    public decimal Days31To60 { get; set; }
    public decimal Days61To90 { get; set; }
    public decimal Days91To120 { get; set; }
    public decimal Over120Days { get; set; }
    public decimal Total { get; set; }
    /// <summary>Alias for Total — matches portal DTO ArAgingSummary.TotalOutstanding</summary>
    public decimal TotalOutstanding => Total;
}
