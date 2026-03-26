using Microsoft.AspNetCore.Mvc;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Controllers;

[ApiController]
[Route("api/v1/ar/adjustments")]
[Produces("application/json")]
public class ArAdjustmentsController : ControllerBase
{
    private readonly IArAdjustmentRepository _adjustmentRepository;
    private readonly IArBalanceRepository _balanceRepository;
    private readonly ILogger<ArAdjustmentsController> _logger;

    public ArAdjustmentsController(
        IArAdjustmentRepository adjustmentRepository,
        IArBalanceRepository balanceRepository,
        ILogger<ArAdjustmentsController> logger)
    {
        _adjustmentRepository = adjustmentRepository;
        _balanceRepository = balanceRepository;
        _logger = logger;
    }

    /// <summary>
    /// Search AR adjustments with optional filters
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ArAdjustment>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ArAdjustment>>> SearchAdjustments(
        [FromQuery] ArAdjustmentType? type = null,
        [FromQuery] ArAdjustmentStatus? status = null,
        [FromQuery] DateTime? period = null,
        [FromQuery] string? glAccountId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var results = await _adjustmentRepository.SearchAsync(type, status, period, glAccountId, page, pageSize);
        return Ok(results);
    }

    /// <summary>
    /// Get AR adjustment by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ArAdjustment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ArAdjustment>> GetAdjustmentById(string id)
    {
        var adjustment = await _adjustmentRepository.GetByIdAsync(id);
        if (adjustment == null)
            return NotFound(new { error = $"AR adjustment {id} not found" });
        return Ok(adjustment);
    }

    /// <summary>
    /// Create a new AR adjustment. AdjustmentNumber is auto-generated: ADJ-{yyyyMMdd}-{seq}
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ArAdjustment), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArAdjustment>> CreateAdjustment([FromBody] ArAdjustment adjustment)
    {
        if (adjustment.Amount <= 0)
            return BadRequest(new { error = "Adjustment amount must be positive. Use Direction (Debit/Credit) to indicate the posting side." });

        // Auto-generate adjustment number
        adjustment.AdjustmentNumber = $"ADJ-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        adjustment.Status = ArAdjustmentStatus.Pending;

        _logger.LogInformation("Creating AR adjustment {AdjustmentNumber}, type={Type}, amount={Amount}",
            SanitizeForLog(adjustment.AdjustmentNumber), adjustment.AdjustmentType, adjustment.Amount);

        var created = await _adjustmentRepository.CreateAsync(adjustment);
        return CreatedAtAction(nameof(GetAdjustmentById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Approve a pending adjustment (Pending -> Approved)
    /// </summary>
    [HttpPost("{id}/approve")]
    [ProducesResponseType(typeof(ArAdjustment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArAdjustment>> ApproveAdjustment(string id, [FromBody] ApproveAdjustmentRequest? request = null)
    {
        var adjustment = await _adjustmentRepository.GetByIdAsync(id);
        if (adjustment == null)
            return NotFound(new { error = $"AR adjustment {id} not found" });

        if (adjustment.Status != ArAdjustmentStatus.Pending)
            return BadRequest(new { error = $"Can only approve Pending adjustments, current: {adjustment.Status}" });

        var authorizedBy = request?.AuthorizedBy;
        if (string.IsNullOrWhiteSpace(authorizedBy))
            authorizedBy = User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(authorizedBy))
            authorizedBy = "system";

        adjustment.Status = ArAdjustmentStatus.Approved;
        adjustment.AuthorizedBy = authorizedBy;
        adjustment.AuthorizedAt = DateTime.UtcNow;
        adjustment.LastUpdatedAt = DateTime.UtcNow;

        _logger.LogInformation("Approved AR adjustment {AdjustmentNumber} by {AuthorizedBy}",
            SanitizeForLog(adjustment.AdjustmentNumber), SanitizeForLog(authorizedBy));

        var updated = await _adjustmentRepository.UpdateAsync(adjustment);
        return Ok(updated);
    }

    /// <summary>
    /// Reject a pending adjustment (Pending -> Rejected)
    /// </summary>
    [HttpPost("{id}/reject")]
    [ProducesResponseType(typeof(ArAdjustment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArAdjustment>> RejectAdjustment(string id, [FromBody] RejectAdjustmentRequest request)
    {
        var adjustment = await _adjustmentRepository.GetByIdAsync(id);
        if (adjustment == null)
            return NotFound(new { error = $"AR adjustment {id} not found" });

        if (adjustment.Status != ArAdjustmentStatus.Pending)
            return BadRequest(new { error = $"Can only reject Pending adjustments, current: {adjustment.Status}" });

        adjustment.Status = ArAdjustmentStatus.Rejected;
        adjustment.Narrative = request.Reason;
        adjustment.LastUpdatedAt = DateTime.UtcNow;

        _logger.LogInformation("Rejected AR adjustment {AdjustmentNumber}: {Reason}",
            adjustment.AdjustmentNumber, SanitizeForLog(request.Reason));

        var updated = await _adjustmentRepository.UpdateAsync(adjustment);
        return Ok(updated);
    }

    /// <summary>
    /// Post an approved adjustment (Approved -> Posted) — adds entry to ArBalance.PostingEntries
    /// </summary>
    [HttpPost("{id}/post")]
    [ProducesResponseType(typeof(ArAdjustment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArAdjustment>> PostAdjustment(string id)
    {
        var adjustment = await _adjustmentRepository.GetByIdAsync(id);
        if (adjustment == null)
            return NotFound(new { error = $"AR adjustment {id} not found" });

        if (adjustment.Status != ArAdjustmentStatus.Approved)
            return BadRequest(new { error = $"Can only post Approved adjustments, current: {adjustment.Status}" });

        // Add posting entry to the target AR balance
        var balance = await _balanceRepository.GetByIdAsync(adjustment.ArBalanceId);
        if (balance == null)
            return BadRequest(new { error = $"Target AR balance {adjustment.ArBalanceId} not found" });

        var entry = new ArPostingEntry
        {
            Source = ArPostingSource.ManualAdjustment,
            SourceReferenceId = adjustment.Id,
            SourceReferenceNumber = adjustment.AdjustmentNumber,
            DebitAmount = adjustment.Direction == ArAdjustmentDirection.Debit ? adjustment.Amount : 0,
            CreditAmount = adjustment.Direction == ArAdjustmentDirection.Credit ? adjustment.Amount : 0,
            PostedAt = DateTime.UtcNow,
            PostedBy = adjustment.AuthorizedBy,
            Memo = adjustment.Narrative
        };

        balance.PostingEntries.Add(entry);
        balance.TotalDebits += entry.DebitAmount;
        balance.TotalCredits += entry.CreditAmount;
        balance.ClosingBalance = balance.OpeningBalance + balance.TotalDebits - balance.TotalCredits;
        balance.LastUpdatedAt = DateTime.UtcNow;
        await _balanceRepository.UpdateAsync(balance);

        adjustment.Status = ArAdjustmentStatus.Posted;
        adjustment.LastUpdatedAt = DateTime.UtcNow;

        _logger.LogInformation("Posted AR adjustment {AdjustmentNumber} to balance {BalanceId}",
            adjustment.AdjustmentNumber, adjustment.ArBalanceId);

        var updated = await _adjustmentRepository.UpdateAsync(adjustment);
        return Ok(updated);
    }

    /// <summary>
    /// Reverse a posted adjustment (Posted -> Reversed)
    /// </summary>
    [HttpPost("{id}/reverse")]
    [ProducesResponseType(typeof(ArAdjustment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArAdjustment>> ReverseAdjustment(string id)
    {
        var adjustment = await _adjustmentRepository.GetByIdAsync(id);
        if (adjustment == null)
            return NotFound(new { error = $"AR adjustment {id} not found" });

        if (adjustment.Status != ArAdjustmentStatus.Posted)
            return BadRequest(new { error = $"Can only reverse Posted adjustments, current: {adjustment.Status}" });

        // Reverse the balance effect — undo what PostAdjustment did
        var balance = await _balanceRepository.GetByIdAsync(adjustment.ArBalanceId);
        if (balance != null)
        {
            var reversalEntry = new ArPostingEntry
            {
                Source = ArPostingSource.ManualAdjustment,
                SourceReferenceId = adjustment.Id,
                SourceReferenceNumber = $"REV-{adjustment.AdjustmentNumber}",
                // Swap debit/credit to reverse the original posting
                DebitAmount = adjustment.Direction == ArAdjustmentDirection.Credit ? adjustment.Amount : 0,
                CreditAmount = adjustment.Direction == ArAdjustmentDirection.Debit ? adjustment.Amount : 0,
                PostedAt = DateTime.UtcNow,
                PostedBy = adjustment.AuthorizedBy,
                Memo = $"Reversal of {adjustment.AdjustmentNumber}"
            };

            balance.PostingEntries.Add(reversalEntry);
            balance.TotalDebits += reversalEntry.DebitAmount;
            balance.TotalCredits += reversalEntry.CreditAmount;
            balance.ClosingBalance = balance.OpeningBalance + balance.TotalDebits - balance.TotalCredits;
            balance.LastUpdatedAt = DateTime.UtcNow;
            await _balanceRepository.UpdateAsync(balance);
        }

        adjustment.Status = ArAdjustmentStatus.Reversed;
        adjustment.LastUpdatedAt = DateTime.UtcNow;

        _logger.LogInformation("Reversed AR adjustment {AdjustmentNumber}",
            SanitizeForLog(adjustment.AdjustmentNumber));

        var updated = await _adjustmentRepository.UpdateAsync(adjustment);
        return Ok(updated);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class ApproveAdjustmentRequest
{
    public string AuthorizedBy { get; set; } = string.Empty;
}

public class RejectAdjustmentRequest
{
    public string Reason { get; set; } = string.Empty;
}
