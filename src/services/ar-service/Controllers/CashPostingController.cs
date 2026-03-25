using Microsoft.AspNetCore.Mvc;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Controllers;

[ApiController]
[Route("api/v1/ar/cash-postings")]
[Produces("application/json")]
public class CashPostingController : ControllerBase
{
    private readonly ICashPostingRepository _cashPostingRepository;
    private readonly ILogger<CashPostingController> _logger;

    public CashPostingController(
        ICashPostingRepository cashPostingRepository,
        ILogger<CashPostingController> logger)
    {
        _cashPostingRepository = cashPostingRepository;
        _logger = logger;
    }

    /// <summary>
    /// Search cash postings with optional filters
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<CashPosting>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CashPosting>>> SearchCashPostings(
        [FromQuery] PayerType? payerType = null,
        [FromQuery] CashPostingStatus? status = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var results = await _cashPostingRepository.SearchAsync(payerType, status, dateFrom, dateTo, page, pageSize);
        return Ok(results);
    }

    /// <summary>
    /// Get cash posting by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(CashPosting), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CashPosting>> GetCashPostingById(string id)
    {
        var posting = await _cashPostingRepository.GetByIdAsync(id);
        if (posting == null)
            return NotFound(new { error = $"Cash posting {id} not found" });
        return Ok(posting);
    }

    /// <summary>
    /// Create a new cash posting. PostingNumber is auto-generated: CP-{yyyyMMdd}-{seq}
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CashPosting), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CashPosting>> CreateCashPosting([FromBody] CashPosting posting)
    {
        // Auto-generate posting number
        posting.PostingNumber = $"CP-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        posting.Status = CashPostingStatus.Pending;

        _logger.LogInformation("Creating cash posting {PostingNumber} for payer {PayerName}",
            SanitizeForLog(posting.PostingNumber), SanitizeForLog(posting.PayerName));

        var created = await _cashPostingRepository.CreateAsync(posting);
        return CreatedAtAction(nameof(GetCashPostingById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Apply a cash posting — sets Status=Applied and computes AppliedAmount from Applications
    /// </summary>
    [HttpPost("{id}/apply")]
    [ProducesResponseType(typeof(CashPosting), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CashPosting>> ApplyCashPosting(string id)
    {
        var posting = await _cashPostingRepository.GetByIdAsync(id);
        if (posting == null)
            return NotFound(new { error = $"Cash posting {id} not found" });

        if (posting.Status == CashPostingStatus.Voided)
            return BadRequest(new { error = "Cannot apply a voided cash posting" });

        if (posting.Status == CashPostingStatus.Applied)
            return BadRequest(new { error = "Cash posting is already applied" });

        // Validate no negative application amounts
        if (posting.Applications.Any(a => a.AmountApplied < 0))
            return BadRequest(new { error = "Application amounts cannot be negative" });

        posting.AppliedAmount = posting.Applications.Sum(a => a.AmountApplied);

        // Guard: total applied cannot exceed receipt amount
        if (posting.AppliedAmount > posting.Amount)
            return BadRequest(new { error = $"Over-application: applied {posting.AppliedAmount:C} exceeds receipt amount {posting.Amount:C}" });

        posting.UnappliedAmount = posting.Amount - posting.AppliedAmount;
        posting.Status = posting.AppliedAmount == posting.Amount
            ? CashPostingStatus.Applied
            : CashPostingStatus.PartiallyApplied;
        posting.LastUpdatedAt = DateTime.UtcNow;

        _logger.LogInformation("Applied cash posting {PostingNumber}, applied={AppliedAmount}",
            posting.PostingNumber, posting.AppliedAmount);

        var updated = await _cashPostingRepository.UpdateAsync(posting);
        return Ok(updated);
    }

    /// <summary>
    /// Void a cash posting
    /// </summary>
    [HttpPost("{id}/void")]
    [ProducesResponseType(typeof(CashPosting), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CashPosting>> VoidCashPosting(string id)
    {
        var posting = await _cashPostingRepository.GetByIdAsync(id);
        if (posting == null)
            return NotFound(new { error = $"Cash posting {id} not found" });

        if (posting.Status == CashPostingStatus.Voided)
            return BadRequest(new { error = "Cash posting is already voided" });

        if (posting.Status == CashPostingStatus.Applied)
            return BadRequest(new { error = "Cannot void an applied cash posting — reverse the application first" });

        posting.Status = CashPostingStatus.Voided;
        posting.LastUpdatedAt = DateTime.UtcNow;

        _logger.LogInformation("Voided cash posting {PostingNumber}", posting.PostingNumber);

        var updated = await _cashPostingRepository.UpdateAsync(posting);
        return Ok(updated);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
