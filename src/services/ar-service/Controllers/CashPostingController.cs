using Microsoft.AspNetCore.Mvc;
using ArService.Ledger;
using ArService.Models;
using ArService.Repositories;
using CloudHealthOffice.Infrastructure.Security;

namespace ArService.Controllers;

[ApiController]
[Route("api/v1/ar/cash-postings")]
[Produces("application/json")]
public class CashPostingController : ControllerBase
{
    private readonly ICashPostingRepository _cashPostingRepository;
    private readonly IArBalanceRepository _balanceRepository;
    private readonly ICurrentActor _actor;
    private readonly ILogger<CashPostingController> _logger;

    public CashPostingController(
        ICashPostingRepository cashPostingRepository,
        IArBalanceRepository balanceRepository,
        ICurrentActor actor,
        ILogger<CashPostingController> logger)
    {
        _cashPostingRepository = cashPostingRepository;
        _balanceRepository = balanceRepository;
        _actor = actor;
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
        posting.CreatedBy = _actor.UserId;
        // Posted state is the server's: a client cannot create a posting that claims to be credited already.
        posting.AppliedAmount = 0m;
        posting.UnappliedAmount = posting.Amount;
        posting.LegacyReconciliation = null;
        foreach (var application in posting.Applications)
        {
            application.PostedEntryId = null;
            application.PostedAt = null;
        }

        _logger.LogInformation("Creating cash posting {PostingNumber} for payer {PayerName}",
            SanitizeForLog(posting.PostingNumber), SanitizeForLog(posting.PayerName));

        var created = await _cashPostingRepository.CreateAsync(posting);
        return CreatedAtAction(nameof(GetCashPostingById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Apply a cash posting: credits each application's amount to its AR
    /// balance (a CashReceipt posting entry, TotalCredits, the sponsor/member
    /// credit split and ClosingBalance), then sets AppliedAmount and the status.
    /// Every referenced balance is checked before anything is written, and an
    /// application already credited is not credited again when a partially
    /// applied posting is applied after more applications were added.
    /// A posting applied before applying credited balances (legacy) is refused
    /// with 409 until finance has reconciled it.
    /// </summary>
    [HttpPost("{id}/apply")]
    [ProducesResponseType(typeof(CashPosting), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CashPosting>> ApplyCashPosting(string id)
    {
        var posting = await _cashPostingRepository.GetByIdAsync(id);
        if (posting == null)
            return NotFound(new { error = $"Cash posting {id} not found" });

        if (posting.Status == CashPostingStatus.Voided)
            return BadRequest(new { error = "Cannot apply a voided cash posting" });

        // Its applications may already have been corrected by hand: crediting them
        // now could credit the balances twice. Finance decides first.
        if (CashPostingLedger.RequiresLegacyReconciliation(posting))
            return LegacyConflict(posting, "applied");

        if (posting.Status == CashPostingStatus.Applied)
            return BadRequest(new { error = "Cash posting is already applied" });

        // Validate no negative application amounts
        if (posting.Applications.Any(a => a.AmountApplied < 0))
            return BadRequest(new { error = "Application amounts cannot be negative" });

        var appliedAmount = posting.Applications.Sum(a => a.AmountApplied);

        // Guard: total applied cannot exceed receipt amount
        if (appliedAmount > posting.Amount)
            return BadRequest(new { error = $"Over-application: applied {appliedAmount:C} exceeds receipt amount {posting.Amount:C}" });

        // Credit the balances. Entry ids are fixed per posting and application, so a
        // retry (after a concurrent save of a balance, or a failure before the posting
        // was saved) finds what was already credited and does not credit it twice.
        var postedBy = _actor.UserId;
        var now = DateTime.UtcNow;
        var pending = posting.Applications.Where(a => a.PostedEntryId == null && a.AmountApplied > 0).ToList();
        var credited = 0;
        for (var attempt = 1; ; attempt++)
        {
            // Every balance an unposted application credits must exist and belong to
            // the application's GL account; all are checked before any is written.
            var balances = new Dictionary<string, ArBalance>(StringComparer.Ordinal);
            foreach (var application in pending)
            {
                if (!balances.TryGetValue(application.ArBalanceId, out var target))
                {
                    target = await _balanceRepository.GetByIdAsync(application.ArBalanceId);
                    if (target == null)
                        return BadRequest(new { error = $"AR balance {application.ArBalanceId} not found" });
                    balances[application.ArBalanceId] = target;
                }
                if (!string.Equals(target.GlAccountId, application.GlAccountId, StringComparison.Ordinal))
                    return BadRequest(new { error = $"AR balance {application.ArBalanceId} belongs to GL account {target.GlAccountId}, not {application.GlAccountId}" });
            }

            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var application in pending)
            {
                var index = posting.Applications.IndexOf(application);
                var entryId = CashPostingLedger.CreditEntryId(posting.Id, index);
                var balance = balances[application.ArBalanceId];
                if (balance.PostingEntries.All(e => e.EntryId != entryId))
                {
                    Post(balance, CashPostingLedger.CreditEntry(posting, index, postedBy, now), posting.PayerType);
                    changed.Add(balance.Id);
                }
                application.PostedEntryId = entryId;
                application.PostedAt ??= now;
            }

            try
            {
                foreach (var balance in balances.Values.Where(b => changed.Contains(b.Id)))
                {
                    balance.LastUpdatedAt = now;
                    await _balanceRepository.UpdateAsync(balance);
                }
                credited = changed.Count;
                break;
            }
            catch (ArConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                // Another writer saved a balance first: re-read and apply on top.
            }
        }

        posting.AppliedAmount = appliedAmount;
        posting.UnappliedAmount = posting.Amount - posting.AppliedAmount;
        posting.Status = posting.AppliedAmount == posting.Amount
            ? CashPostingStatus.Applied
            : CashPostingStatus.PartiallyApplied;
        posting.LastUpdatedAt = now;

        _logger.LogInformation("Applied cash posting {PostingNumber}, applied={AppliedAmount}, credited {Count} balance(s)",
            SanitizeForLog(posting.PostingNumber), posting.AppliedAmount, credited);

        var updated = await _cashPostingRepository.UpdateAsync(posting);
        return Ok(updated);
    }

    /// <summary>How many times a balance save is retried against concurrent writers.</summary>
    internal const int MaxConcurrencyAttempts = 5;

    /// <summary>See <see cref="CashPostingLedger.Post"/>.</summary>
    internal static void Post(ArBalance balance, ArPostingEntry entry, PayerType payerType) =>
        CashPostingLedger.Post(balance, entry, payerType);

    private ObjectResult LegacyConflict(CashPosting posting, string action)
    {
        _logger.LogWarning("Refused: legacy cash posting {PostingNumber} cannot be {Action} before reconciliation",
            SanitizeForLog(posting.PostingNumber), action);
        return Conflict(new
        {
            error = $"Cash posting {posting.PostingNumber} was applied before applying credited AR balances. " +
                    $"Its applications were never credited and may have been corrected by hand, so it cannot be {action} " +
                    $"until finance has reconciled it (see {CashPostingLedger.RunbookPath}).",
            code = CashPostingLedger.LegacyPostingRequiresReconciliation,
            postingId = posting.Id,
            runbook = CashPostingLedger.RunbookPath
        });
    }

    /// <summary>
    /// Void a cash posting. Reverses only credits this service posted: an application
    /// never credited (legacy, or marked <c>manual-</c> by the reconciliation) is not
    /// debited. A legacy posting is refused with 409 until finance has reconciled it.
    /// </summary>
    [HttpPost("{id}/void")]
    [ProducesResponseType(typeof(CashPosting), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CashPosting>> VoidCashPosting(string id)
    {
        var posting = await _cashPostingRepository.GetByIdAsync(id);
        if (posting == null)
            return NotFound(new { error = $"Cash posting {id} not found" });

        if (posting.Status == CashPostingStatus.Voided)
            return BadRequest(new { error = "Cash posting is already voided" });

        // Voiding would drop it out of finance's review while any manual correction
        // stays on the balance; finance decides first.
        if (CashPostingLedger.RequiresLegacyReconciliation(posting))
            return LegacyConflict(posting, "voided");

        if (posting.Status == CashPostingStatus.Applied)
            return BadRequest(new { error = "Cannot void an applied cash posting — reverse the application first" });

        // A partially applied posting has credited balances: debit them back first.
        // Each reversal has a fixed id (rev-{entry}) and debits what the original
        // entry credited, so a retried void never reverses twice.
        // A manual-… id marks an application finance corrected by hand: nothing was
        // credited here, so there is nothing to reverse (finance reverses its own adjustment).
        var posted = posting.Applications.Where(a => a.PostedEntryId != null && !CashPostingLedger.IsManualEntryId(a.PostedEntryId)).ToList();
        var manual = posting.Applications.Count(a => CashPostingLedger.IsManualEntryId(a.PostedEntryId));
        var now = DateTime.UtcNow;
        for (var attempt = 1; ; attempt++)
        {
            var balances = new Dictionary<string, ArBalance>(StringComparer.Ordinal);
            foreach (var application in posted)
            {
                if (balances.ContainsKey(application.ArBalanceId))
                    continue;
                var balance = await _balanceRepository.GetByIdAsync(application.ArBalanceId);
                if (balance == null)
                    return BadRequest(new { error = $"AR balance {application.ArBalanceId} credited by this posting no longer exists; cannot reverse it" });
                balances[application.ArBalanceId] = balance;
            }

            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var application in posted)
            {
                var balance = balances[application.ArBalanceId];
                var reversalId = $"rev-{application.PostedEntryId}";
                if (balance.PostingEntries.Any(e => e.EntryId == reversalId))
                    continue;
                var original = balance.PostingEntries.FirstOrDefault(e => e.EntryId == application.PostedEntryId);
                if (original == null)
                    continue; // never reached the balance: nothing to reverse
                Post(balance, new ArPostingEntry
                {
                    EntryId = reversalId,
                    Source = ArPostingSource.CashReceipt,
                    SourceReferenceId = posting.Id,
                    SourceReferenceNumber = $"REV-{posting.PostingNumber}",
                    DebitAmount = original.CreditAmount,
                    PostedAt = now,
                    PostedBy = _actor.UserId,
                    Memo = $"Void of {posting.PostingNumber} (reverses entry {original.EntryId})",
                    MemberId = original.MemberId
                }, posting.PayerType);
                changed.Add(balance.Id);
            }

            try
            {
                foreach (var balance in balances.Values.Where(b => changed.Contains(b.Id)))
                {
                    balance.LastUpdatedAt = now;
                    await _balanceRepository.UpdateAsync(balance);
                }
                break;
            }
            catch (ArConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
            }
        }

        posting.Status = CashPostingStatus.Voided;
        posting.LastUpdatedAt = DateTime.UtcNow;

        _logger.LogInformation("Voided cash posting {PostingNumber}, reversed {Count} credited application(s)",
            SanitizeForLog(posting.PostingNumber), posted.Count);
        if (manual > 0)
            _logger.LogWarning("Voided cash posting {PostingNumber}: {Manual} application(s) were corrected by hand at reconciliation and were not reversed; finance must reverse those adjustments",
                SanitizeForLog(posting.PostingNumber), manual);

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
