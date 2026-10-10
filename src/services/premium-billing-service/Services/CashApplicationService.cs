using CloudHealthOffice.Infrastructure.Security;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Services;

public interface ICashApplicationService
{
    /// <summary>
    /// Records the payment and applies each remittance item to the invoice
    /// its reference names: an exact payment pays the invoice, a short one
    /// leaves a balance, an excess pays the balance and goes to the sponsor's
    /// unapplied credit; anything that cannot be matched goes to the
    /// exceptions queue. Invoice balances and sponsor account balances are
    /// saved as each item is applied.
    /// </summary>
    /// <exception cref="DuplicateRemittanceException">The payment was already recorded.</exception>
    Task<RemittanceBatch> ApplyAsync(RemittanceAdvice advice);

    /// <summary>Resolves an open exceptions-queue item by applying, crediting or dismissing it.</summary>
    Task<RemittanceException> ResolveExceptionAsync(string exceptionId, ResolveRemittanceExceptionRequest request);
}

public enum RemittanceExceptionAction
{
    ApplyToInvoice,
    CreditSponsorAccount,
    Dismiss
}

public class ResolveRemittanceExceptionRequest
{
    public RemittanceExceptionAction Action { get; set; }

    /// <summary>ApplyToInvoice: the invoice (id) to apply the cash to.</summary>
    public string? InvoiceId { get; set; }

    /// <summary>CreditSponsorAccount: the group whose account receives the credit.</summary>
    public string? GroupNumber { get; set; }

    /// <summary>Why; required.</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>The payment (same source, payer and trace number) was already recorded.</summary>
public sealed class DuplicateRemittanceException : Exception
{
    public string BatchId { get; }

    public DuplicateRemittanceException(string batchId, string traceNumber)
        : base($"Payment with trace number {traceNumber} was already received (batch {batchId}); it was not posted again")
    {
        BatchId = batchId;
    }
}

public sealed class CashApplicationService : ICashApplicationService
{
    /// <summary>BPR01 codes for an 820 that moves money. I (remittance only) and P (prenote) post nothing.</summary>
    private static readonly HashSet<string> MoneyMovingHandlingCodes = new(StringComparer.OrdinalIgnoreCase) { "C", "D", "U", "X" };

    /// <summary>
    /// RMR01 qualifiers whose RMR02 is an invoice number: IK (invoice), IV
    /// (seller's invoice number), OI (original invoice number), 11 (account
    /// receivable number). Other qualifiers (AZ policy, 1L group policy, ...)
    /// do not name an invoice and go to the exceptions queue. A missing
    /// qualifier (lockbox) is treated as an invoice number.
    /// </summary>
    internal static readonly HashSet<string> InvoiceReferenceQualifiers = new(StringComparer.OrdinalIgnoreCase) { "IK", "IV", "OI", "11" };

    private const int MaxConcurrencyAttempts = 10;

    private readonly IRemittanceBatchRepository _batches;
    private readonly IRemittanceExceptionRepository _exceptions;
    private readonly IPremiumInvoiceRepository _invoices;
    private readonly ISponsorAccountRepository _accounts;
    private readonly ICurrentActor _actor;
    private readonly ILogger<CashApplicationService> _logger;

    public CashApplicationService(
        IRemittanceBatchRepository batches,
        IRemittanceExceptionRepository exceptions,
        IPremiumInvoiceRepository invoices,
        ISponsorAccountRepository accounts,
        ICurrentActor actor,
        ILogger<CashApplicationService> logger)
    {
        _batches = batches;
        _exceptions = exceptions;
        _invoices = invoices;
        _accounts = accounts;
        _actor = actor;
        _logger = logger;
    }

    public async Task<RemittanceBatch> ApplyAsync(RemittanceAdvice advice)
    {
        ArgumentNullException.ThrowIfNull(advice);
        if (string.IsNullOrWhiteSpace(advice.TraceNumber))
            throw new ArgumentException("The payment has no trace number");
        if (string.IsNullOrWhiteSpace(advice.PayerId))
            throw new ArgumentException("The payment names no payer");
        if (advice.PaymentAmount < 0)
            throw new ArgumentException("The payment amount is negative");

        var paymentDate = advice.PaymentDate == default ? DateTime.UtcNow.Date : advice.PaymentDate;
        var batch = new RemittanceBatch
        {
            Id = RemittanceBatch.IdFor(_actor.TenantId, advice.Source, advice.PayerId, advice.TraceNumber),
            Source = advice.Source,
            SourceFileName = advice.SourceFileName,
            TraceNumber = advice.TraceNumber.Trim(),
            PayerId = advice.PayerId.Trim(),
            PayerName = advice.PayerName,
            TransactionHandlingCode = advice.TransactionHandlingCode,
            CreditDebitFlag = advice.CreditDebitFlag,
            GroupReference = advice.GroupReference,
            PaymentMethod = advice.PaymentMethod,
            CheckNumber = advice.CheckNumber,
            PaymentAmount = advice.PaymentAmount,
            PaymentDate = paymentDate,
            CrossSourceKey = RemittanceBatch.CrossSourceKeyFor(advice.CheckNumber, advice.TraceNumber, advice.PaymentAmount, paymentDate),
            Warnings = advice.Warnings.ToList(),
            ProcessedBy = _actor.UserId,
            Status = RemittanceBatchStatus.Processing
        };

        // Recording the batch first is the duplicate guard: its id is the payment's identity.
        // A batch left Processing (the upload failed part-way) is resumed, not refused:
        // every step below is idempotent, and completed items are recorded on the batch.
        if (!await _batches.TryCreateAsync(batch))
        {
            var existing = await _batches.GetByIdAsync(batch.Id)
                           ?? throw new DuplicateRemittanceException(batch.Id, batch.TraceNumber);
            if (existing.Status != RemittanceBatchStatus.Processing)
                throw new DuplicateRemittanceException(batch.Id, batch.TraceNumber);
            _logger.LogWarning("Resuming remittance {BatchId} (trace {Trace}): {Done} of {Total} item(s) were already done",
                existing.Id, Sanitize(existing.TraceNumber), existing.Applications.Count, advice.Items.Count);
            existing.Warnings.Add($"Resumed at {DateTime.UtcNow:O} by {_actor.UserId}");
            batch = existing;
        }

        if (advice.Source == RemittanceSource.X12820 && !MoneyMovingHandlingCodes.Contains(advice.TransactionHandlingCode ?? string.Empty))
        {
            batch.Warnings.Add($"BPR01 '{advice.TransactionHandlingCode}' moves no money; nothing was posted");
            return await CompleteAsync(batch, RemittanceBatchStatus.NotPosted);
        }
        if (string.Equals(advice.CreditDebitFlag, "D", StringComparison.OrdinalIgnoreCase))
        {
            batch.Warnings.Add("BPR03 is D (a debit, not a credit to the payee); nothing was posted");
            return await CompleteAsync(batch, RemittanceBatchStatus.NotPosted);
        }

        // Whole-payment checks: any of these sends the entire payment to the queue, unapplied.
        var otherSource = (await _batches.FindByCrossSourceKeyAsync(batch.CrossSourceKey!))
            .FirstOrDefault(b => b.Id != batch.Id && b.Source != batch.Source && b.Status != RemittanceBatchStatus.NotPosted);
        var detailTotal = advice.Items.Sum(i => i.Amount);
        (RemittanceExceptionReason Reason, string Detail)? hold =
            otherSource != null
                ? (RemittanceExceptionReason.PossibleDuplicate,
                    $"Check/trace {batch.CheckNumber ?? batch.TraceNumber} for {batch.PaymentAmount:0.00} on {batch.PaymentDate:yyyy-MM-dd} already arrived by {otherSource.Source} (batch {otherSource.Id}); not posted again")
            : advice.Items.Any(i => i.Amount < 0)
                ? (RemittanceExceptionReason.NegativeLineInPayment,
                    "The payment has a negative item (reversal or recoupment); no item was applied, so applied cash cannot exceed the money received")
            : detailTotal > advice.PaymentAmount
                ? (RemittanceExceptionReason.DetailExceedsPayment,
                    $"Remittance detail totals {detailTotal:0.00} but the payment is {advice.PaymentAmount:0.00}; no item was applied")
            : null;
        if (hold is { } h)
        {
            var exception = await QueueAsync(batch, null, advice.PaymentAmount, h.Reason, h.Detail);
            batch.Applications = advice.Items.Select(item => ExceptionRow(item, exception.Id)).ToList();
            return await CompleteAsync(batch, RemittanceBatchStatus.Completed);
        }

        var done = batch.Applications.Select(a => a.LineNumber).ToHashSet();
        foreach (var item in advice.Items.OrderBy(i => i.LineNumber))
        {
            if (done.Contains(item.LineNumber))
                continue;
            batch.Applications.Add(await ApplyItemAsync(batch, item));
            await _batches.UpdateAsync(batch); // progress: a failure after this resumes at the next item
        }

        var remainder = advice.PaymentAmount - detailTotal;
        if (remainder > 0)
            await QueueAsync(batch, null, remainder, RemittanceExceptionReason.UnallocatedRemainder,
                advice.Items.Count == 0
                    ? "The payment has no remittance detail"
                    : $"Payment {advice.PaymentAmount:0.00} exceeds the remittance detail {detailTotal:0.00}");

        foreach (var group in batch.Applications.Where(a => a.GroupNumber != null).Select(a => a.GroupNumber!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            await SponsorAccountBalances.RefreshAsync(_invoices, _accounts, group, batch.PaymentDate);

        return await CompleteAsync(batch, RemittanceBatchStatus.Completed);
    }

    public async Task<RemittanceException> ResolveExceptionAsync(string exceptionId, ResolveRemittanceExceptionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Note))
            throw new ArgumentException("A note explaining the resolution is required");

        var item = await _exceptions.GetByIdAsync(exceptionId)
                   ?? throw new KeyNotFoundException($"Exception {exceptionId} not found");
        if (item.Status != RemittanceExceptionStatus.Open)
            throw new InvalidOperationException($"Exception {exceptionId} is already {item.Status}");

        // Checks that need no claim.
        PremiumInvoice? invoice = null;
        string? group = null;
        switch (request.Action)
        {
            case RemittanceExceptionAction.ApplyToInvoice:
                if (item.Amount <= 0)
                    throw new InvalidOperationException("Only a positive amount can be applied to an invoice");
                invoice = await _invoices.GetByIdAsync(request.InvoiceId ?? string.Empty)
                          ?? throw new KeyNotFoundException($"Invoice {request.InvoiceId} not found");
                if (invoice.Status is InvoiceStatus.Voided or InvoiceStatus.WriteOff or InvoiceStatus.Draft)
                    throw new InvalidOperationException($"Invoice {invoice.InvoiceNumber} is {invoice.Status}");
                break;
            case RemittanceExceptionAction.CreditSponsorAccount:
                if (item.Amount <= 0)
                    throw new InvalidOperationException("Only a positive amount can be credited");
                if (string.IsNullOrWhiteSpace(request.GroupNumber))
                    throw new ArgumentException("GroupNumber is required to credit a sponsor account");
                group = request.GroupNumber.Trim();
                if (!await SponsorAccountBalances.GroupExistsAsync(_invoices, _accounts, group))
                    throw new ArgumentException($"Group {group} has no invoices or account; check the group number");
                break;
            case RemittanceExceptionAction.Dismiss:
                break;
            default:
                throw new ArgumentException($"Unknown action {request.Action}");
        }

        // Claim it: only one person can resolve it, even when two act at once.
        if (!await _exceptions.TryTransitionAsync(item.Id, RemittanceExceptionStatus.Open, RemittanceExceptionStatus.Resolving))
            throw new InvalidOperationException($"Exception {exceptionId} is already being resolved or was resolved");
        item.Status = RemittanceExceptionStatus.Resolving;

        try
        {
            switch (request.Action)
            {
                case RemittanceExceptionAction.ApplyToInvoice:
                {
                    var posting = await PostToInvoiceAsync(invoice!.Id, item.Amount, item.PaymentDate, item.TraceNumber,
                        p => p.RemittanceExceptionId == item.Id,
                        p => { p.RemittanceBatchId = item.BatchId; p.RemittanceExceptionId = item.Id; p.MemberId = item.MemberId; })
                        ?? throw new InvalidOperationException($"Invoice {invoice.InvoiceNumber} was closed meanwhile");
                    if (posting.Excess > 0)
                        await CreditAsync(posting.GroupNumber, $"exc-{item.Id}", posting.Excess, SponsorAccountEntryType.OverpaymentCredit,
                            item.BatchId, item.TraceNumber, invoice.Id, item.Reference, $"Excess over invoice {invoice.InvoiceNumber} (exception {item.Id})");
                    await SponsorAccountBalances.RefreshAsync(_invoices, _accounts, posting.GroupNumber, item.PaymentDate);
                    item.Status = RemittanceExceptionStatus.Applied;
                    item.AppliedInvoiceId = invoice.Id;
                    break;
                }
                case RemittanceExceptionAction.CreditSponsorAccount:
                    await CreditAsync(group!, $"exc-{item.Id}", item.Amount, SponsorAccountEntryType.ExceptionCredit,
                        item.BatchId, item.TraceNumber, null, item.Reference, $"Exception {item.Id}: {Sanitize(request.Note)}");
                    await SponsorAccountBalances.RefreshAsync(_invoices, _accounts, group!, item.PaymentDate);
                    item.Status = RemittanceExceptionStatus.Credited;
                    item.CreditedGroupNumber = group;
                    break;
                case RemittanceExceptionAction.Dismiss:
                    item.Status = RemittanceExceptionStatus.Dismissed;
                    break;
            }
        }
        catch
        {
            // Nothing was finished: give it back to the queue. Posting is idempotent per exception, so a retry is safe.
            await _exceptions.TryTransitionAsync(item.Id, RemittanceExceptionStatus.Resolving, RemittanceExceptionStatus.Open);
            throw;
        }

        item.ResolvedAt = DateTime.UtcNow;
        item.ResolvedBy = _actor.UserId;
        item.ResolutionNote = request.Note.Length > 1000 ? request.Note[..1000] : request.Note;
        _logger.LogInformation("Remittance exception {ExceptionId} resolved as {Status} by {Actor}",
            item.Id, item.Status, Sanitize(_actor.UserId));
        return await _exceptions.UpdateAsync(item);
    }

    private async Task<RemittanceApplication> ApplyItemAsync(RemittanceBatch batch, RemittanceItem item)
    {
        var reference = item.Reference?.Trim();
        if (item.Amount == 0)
        {
            batch.Warnings.Add($"Line {item.LineNumber}: zero amount, nothing applied");
            return new RemittanceApplication
            {
                LineNumber = item.LineNumber, Level = item.Level, Reference = reference, MemberId = item.MemberId,
                Outcome = CashApplicationOutcome.Skipped
            };
        }
        if (string.IsNullOrEmpty(reference))
            return await QueueRowAsync(batch, item, RemittanceExceptionReason.MissingReference, "The item has no invoice reference");
        if (item.ReferenceQualifier != null && !InvoiceReferenceQualifiers.Contains(item.ReferenceQualifier))
            return await QueueRowAsync(batch, item, RemittanceExceptionReason.UnsupportedReferenceQualifier,
                $"RMR01 '{item.ReferenceQualifier}' does not identify an invoice");

        var candidates = (await _invoices.GetByInvoiceNumberAsync(reference)).ToList();
        if (candidates.Count == 0 && !string.Equals(reference, reference.ToUpperInvariant(), StringComparison.Ordinal))
            candidates = (await _invoices.GetByInvoiceNumberAsync(reference.ToUpperInvariant())).ToList();
        var open = candidates.Where(i => i.Status is not (InvoiceStatus.Voided or InvoiceStatus.WriteOff or InvoiceStatus.Draft)).ToList();

        if (candidates.Count == 0)
            return await QueueRowAsync(batch, item, RemittanceExceptionReason.InvoiceNotFound, $"No invoice numbered {reference}");
        if (open.Count == 0)
            return await QueueRowAsync(batch, item, RemittanceExceptionReason.InvoiceClosed, $"Invoice {reference} is {candidates[0].Status}");
        if (open.Count > 1)
            return await QueueRowAsync(batch, item, RemittanceExceptionReason.AmbiguousReference, $"{open.Count} open invoices are numbered {reference}");
        if (!string.IsNullOrWhiteSpace(batch.GroupReference)
            && !string.Equals(open[0].GroupNumber, batch.GroupReference.Trim(), StringComparison.OrdinalIgnoreCase))
            return await QueueRowAsync(batch, item, RemittanceExceptionReason.GroupMismatch,
                $"Invoice {reference} is group {open[0].GroupNumber}; the payer named group {batch.GroupReference}");

        var line = item.LineNumber;
        var posting = await PostToInvoiceAsync(open[0].Id, item.Amount, batch.PaymentDate, batch.TraceNumber,
            p => p.RemittanceBatchId == batch.Id && p.RemittanceLine == line,
            p => { p.RemittanceBatchId = batch.Id; p.RemittanceLine = line; p.MemberId = item.MemberId; });
        if (posting == null)
            return await QueueRowAsync(batch, item, RemittanceExceptionReason.InvoiceClosed, $"Invoice {reference} was closed while posting");

        if (posting.Excess > 0)
            await CreditAsync(posting.GroupNumber, $"{batch.Id}-{line}", posting.Excess, SponsorAccountEntryType.OverpaymentCredit,
                batch.Id, batch.TraceNumber, posting.InvoiceId, reference, $"Overpayment of invoice {posting.InvoiceNumber}");

        _logger.LogInformation(
            "Applied {Amount:0.00} from trace {Trace} line {Line} to invoice {InvoiceNumber}: applied {Applied:0.00}, credit {Excess:0.00}",
            item.Amount, Sanitize(batch.TraceNumber), line, posting.InvoiceNumber, posting.Applied, posting.Excess);

        return new RemittanceApplication
        {
            LineNumber = line,
            Level = item.Level,
            Reference = reference,
            MemberId = item.MemberId,
            PaidAmount = item.Amount,
            InvoiceId = posting.InvoiceId,
            InvoiceNumber = posting.InvoiceNumber,
            GroupNumber = posting.GroupNumber,
            BalanceBefore = posting.BalanceBefore,
            AppliedAmount = posting.Applied,
            UnappliedCreditAmount = posting.Excess,
            PaymentId = posting.PaymentId,
            Outcome = posting.Excess > 0 ? CashApplicationOutcome.Overpayment
                : posting.Applied == posting.BalanceBefore ? CashApplicationOutcome.ExactMatch
                : CashApplicationOutcome.PartialPayment
        };
    }

    internal sealed record Posting(string InvoiceId, string InvoiceNumber, string GroupNumber,
        decimal BalanceBefore, decimal Applied, decimal Excess, string? PaymentId);

    /// <summary>
    /// Applies up to the invoice's current balance as a payment and returns the
    /// excess (to be held as unapplied credit). The invoice is re-read and the
    /// split recomputed on every attempt, and saved with optimistic concurrency,
    /// so a concurrent payment is never overwritten and never over-applied. If
    /// the payment is already on the invoice (<paramref name="isThisPayment"/>),
    /// it is not added again. Null when the invoice is voided or written off.
    /// </summary>
    private async Task<Posting?> PostToInvoiceAsync(string invoiceId, decimal amount, DateTime paymentDate, string trace,
        Func<InvoicePayment, bool> isThisPayment, Action<InvoicePayment> mark)
    {
        for (var attempt = 1; ; attempt++)
        {
            var invoice = await _invoices.GetByIdAsync(invoiceId)
                          ?? throw new InvalidOperationException($"Invoice {invoiceId} not found");
            if (invoice.Status is InvoiceStatus.Voided or InvoiceStatus.WriteOff or InvoiceStatus.Draft)
                return null;

            var already = invoice.Payments.FirstOrDefault(isThisPayment);
            if (already != null)
            {
                invoice.RecalculateTotals();
                return new Posting(invoice.Id, invoice.InvoiceNumber, invoice.GroupNumber,
                    invoice.BalanceDue + already.Amount, already.Amount, amount - already.Amount, already.PaymentId);
            }

            invoice.RecalculateTotals();
            var balanceBefore = invoice.BalanceDue;
            var applied = Math.Min(amount, Math.Max(balanceBefore, 0m));
            var excess = amount - applied;
            string? paymentId = null;
            if (applied > 0)
            {
                var payment = new InvoicePayment
                {
                    Amount = applied,
                    PaymentDate = paymentDate,
                    PaymentMethod = "Remittance",
                    ReferenceNumber = trace,
                    ReceivedDate = DateTime.UtcNow,
                    RecordedBy = _actor.UserId
                };
                mark(payment);
                invoice.Payments.Add(payment);
                invoice.RecalculateTotals();
                PremiumInvoiceStatus.ApplyPaymentStatus(invoice);
                invoice.LastUpdatedBy = _actor.UserId;
                paymentId = payment.PaymentId;
                try
                {
                    await _invoices.UpdateAsync(invoice);
                }
                catch (ConcurrencyConflictException) when (attempt < MaxConcurrencyAttempts)
                {
                    continue; // someone else changed the invoice: re-read and recompute the split
                }
            }
            return new Posting(invoice.Id, invoice.InvoiceNumber, invoice.GroupNumber, balanceBefore, applied, excess, paymentId);
        }
    }

    /// <summary>Adds unapplied credit once per <paramref name="entryId"/> (a resumed batch does not credit twice).</summary>
    private Task<SponsorAccount> CreditAsync(string groupNumber, string entryId, decimal amount, SponsorAccountEntryType type,
        string? batchId, string? trace, string? invoiceId, string? reference, string memo)
    {
        var postedBy = _actor.UserId;
        return _accounts.UpdateAsync(groupNumber, account =>
        {
            if (account.Entries.Any(e => e.EntryId == entryId))
                return;
            account.UnappliedCredit += amount;
            account.NetBalance = account.OpenInvoiceBalance - account.UnappliedCredit;
            account.Entries.Add(new SponsorAccountEntry
            {
                EntryId = entryId,
                Type = type,
                Amount = amount,
                BatchId = batchId,
                TraceNumber = trace,
                InvoiceId = invoiceId,
                Reference = reference,
                Memo = memo.Length > 500 ? memo[..500] : memo,
                PostedBy = postedBy
            });
        });
    }

    private async Task<RemittanceApplication> QueueRowAsync(RemittanceBatch batch, RemittanceItem item,
        RemittanceExceptionReason reason, string detail) =>
        ExceptionRow(item, (await QueueAsync(batch, item, item.Amount, reason, detail)).Id);

    private async Task<RemittanceException> QueueAsync(RemittanceBatch batch, RemittanceItem? item, decimal amount,
        RemittanceExceptionReason reason, string detail)
    {
        var line = item?.LineNumber ?? 0;
        var exception = await _exceptions.CreateAsync(new RemittanceException
        {
            Id = RemittanceException.IdFor(batch.Id, line, reason),
            BatchId = batch.Id,
            Source = batch.Source,
            TraceNumber = batch.TraceNumber,
            PayerId = batch.PayerId,
            PayerName = batch.PayerName,
            PaymentDate = batch.PaymentDate,
            LineNumber = line,
            Reference = item?.Reference?.Trim(),
            MemberId = item?.MemberId,
            Amount = amount,
            Reason = reason,
            Detail = detail
        });
        if (batch.Exceptions.All(e => e.Id != exception.Id))
            batch.Exceptions.Add(new BatchExceptionRef { Id = exception.Id, Amount = exception.Amount });
        _logger.LogWarning("Remittance trace {Trace} line {Line}: {Amount:0.00} queued as {Reason}",
            Sanitize(batch.TraceNumber), line, amount, reason);
        return exception;
    }

    private static RemittanceApplication ExceptionRow(RemittanceItem item, string exceptionId) => new()
    {
        LineNumber = item.LineNumber,
        Level = item.Level,
        Reference = item.Reference?.Trim(),
        MemberId = item.MemberId,
        PaidAmount = item.Amount,
        Outcome = CashApplicationOutcome.Exception,
        ExceptionId = exceptionId
    };

    private async Task<RemittanceBatch> CompleteAsync(RemittanceBatch batch, RemittanceBatchStatus status)
    {
        batch.AppliedAmount = batch.Applications.Sum(a => a.AppliedAmount);
        batch.UnappliedCreditAmount = batch.Applications.Sum(a => a.UnappliedCreditAmount);
        batch.ExceptionCount = batch.Exceptions.Count;
        batch.ExceptionAmount = batch.Exceptions.Sum(e => e.Amount);
        batch.Status = status;
        batch.CompletedAt = DateTime.UtcNow;
        _logger.LogInformation(
            "Remittance {BatchId} ({Source}, trace {Trace}) {Status}: received {Amount:0.00}, applied {Applied:0.00}, credit {Credit:0.00}, exceptions {Exceptions:0.00}",
            batch.Id, batch.Source, Sanitize(batch.TraceNumber), status, batch.PaymentAmount, batch.AppliedAmount,
            batch.UnappliedCreditAmount, batch.ExceptionAmount);
        return await _batches.UpdateAsync(batch);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>Invoice status after a payment changes its balance.</summary>
public static class PremiumInvoiceStatus
{
    public static void ApplyPaymentStatus(PremiumInvoice invoice)
    {
        if (invoice.BalanceDue <= 0)
            invoice.Status = InvoiceStatus.Paid;
        else if (invoice.TotalPaid > 0)
            invoice.Status = InvoiceStatus.PartiallyPaid;
    }
}

/// <summary>Recomputes a sponsor account's open invoice balance from its invoices.</summary>
public static class SponsorAccountBalances
{
    public static async Task<SponsorAccount> RefreshAsync(IPremiumInvoiceRepository invoices, ISponsorAccountRepository accounts,
        string groupNumber, DateTime? lastPaymentAt)
    {
        var open = (await invoices.GetByGroupNumberAsync(groupNumber))
            .Where(i => i.Status is not (InvoiceStatus.Voided or InvoiceStatus.WriteOff or InvoiceStatus.Draft) && i.BalanceDue > 0)
            .Sum(i => i.BalanceDue);
        return await accounts.UpdateAsync(groupNumber, account =>
        {
            account.OpenInvoiceBalance = open;
            account.NetBalance = open - account.UnappliedCredit;
            if (lastPaymentAt.HasValue && (account.LastPaymentAt == null || lastPaymentAt > account.LastPaymentAt))
                account.LastPaymentAt = lastPaymentAt;
        });
    }

    /// <summary>A group is known when it has an invoice or an account; an unknown number is a typo, not a new account.</summary>
    public static async Task<bool> GroupExistsAsync(IPremiumInvoiceRepository invoices, ISponsorAccountRepository accounts, string groupNumber) =>
        (await invoices.GetByGroupNumberAsync(groupNumber)).Any() || await accounts.GetAsync(groupNumber) != null;
}
