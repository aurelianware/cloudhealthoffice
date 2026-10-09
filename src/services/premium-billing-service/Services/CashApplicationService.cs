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

        var batch = new RemittanceBatch
        {
            Id = RemittanceBatch.IdFor(_actor.TenantId, advice.Source, advice.PayerId, advice.TraceNumber),
            Source = advice.Source,
            SourceFileName = advice.SourceFileName,
            TraceNumber = advice.TraceNumber.Trim(),
            PayerId = advice.PayerId.Trim(),
            PayerName = advice.PayerName,
            TransactionHandlingCode = advice.TransactionHandlingCode,
            PaymentMethod = advice.PaymentMethod,
            CheckNumber = advice.CheckNumber,
            PaymentAmount = advice.PaymentAmount,
            PaymentDate = advice.PaymentDate == default ? DateTime.UtcNow.Date : advice.PaymentDate,
            Warnings = advice.Warnings.ToList(),
            ProcessedBy = _actor.UserId,
            Status = RemittanceBatchStatus.Processing
        };

        // Recording the batch first is the duplicate guard: its id is the payment's identity.
        if (!await _batches.TryCreateAsync(batch))
            throw new DuplicateRemittanceException(batch.Id, batch.TraceNumber);

        if (advice.Source == RemittanceSource.X12820 && !MoneyMovingHandlingCodes.Contains(advice.TransactionHandlingCode ?? string.Empty))
        {
            batch.Warnings.Add($"BPR01 '{advice.TransactionHandlingCode}' moves no money; nothing was posted");
            return await CompleteAsync(batch, RemittanceBatchStatus.NotPosted);
        }

        var detailTotal = advice.Items.Sum(i => i.Amount);
        if (detailTotal > advice.PaymentAmount)
        {
            // The detail claims more than was received: posting it would overstate cash.
            var exception = await QueueAsync(batch, null, advice.PaymentAmount, RemittanceExceptionReason.DetailExceedsPayment,
                $"Remittance detail totals {detailTotal:0.00} but the payment is {advice.PaymentAmount:0.00}; no item was applied");
            foreach (var item in advice.Items)
                batch.Applications.Add(ExceptionRow(item, exception.Id));
            return await CompleteAsync(batch, RemittanceBatchStatus.Completed);
        }

        var touchedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in advice.Items.OrderBy(i => i.LineNumber))
            batch.Applications.Add(await ApplyItemAsync(batch, item, touchedGroups));

        var remainder = advice.PaymentAmount - detailTotal;
        if (remainder > 0)
            await QueueAsync(batch, null, remainder, RemittanceExceptionReason.UnallocatedRemainder,
                advice.Items.Count == 0
                    ? "The payment has no remittance detail"
                    : $"Payment {advice.PaymentAmount:0.00} exceeds the remittance detail {detailTotal:0.00}");

        foreach (var group in touchedGroups)
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

        switch (request.Action)
        {
            case RemittanceExceptionAction.ApplyToInvoice:
            {
                if (item.Amount <= 0)
                    throw new InvalidOperationException("Only a positive amount can be applied to an invoice");
                var invoice = await _invoices.GetByIdAsync(request.InvoiceId ?? string.Empty)
                              ?? throw new KeyNotFoundException($"Invoice {request.InvoiceId} not found");
                if (invoice.Status is InvoiceStatus.Voided or InvoiceStatus.WriteOff)
                    throw new InvalidOperationException($"Invoice {invoice.InvoiceNumber} is {invoice.Status}");

                var posting = PostToInvoice(invoice, item.Amount, item.PaymentDate, item.TraceNumber, item.BatchId, item.LineNumber, item.MemberId);
                if (posting.Applied > 0)
                    await _invoices.UpdateAsync(invoice);
                if (posting.Excess > 0)
                    await CreditAsync(invoice.GroupNumber, posting.Excess, SponsorAccountEntryType.OverpaymentCredit,
                        item.BatchId, item.TraceNumber, invoice.Id, item.Reference, $"Excess over invoice {invoice.InvoiceNumber} (exception {item.Id})");
                await SponsorAccountBalances.RefreshAsync(_invoices, _accounts, invoice.GroupNumber, item.PaymentDate);

                item.Status = RemittanceExceptionStatus.Applied;
                item.AppliedInvoiceId = invoice.Id;
                break;
            }
            case RemittanceExceptionAction.CreditSponsorAccount:
            {
                if (item.Amount <= 0)
                    throw new InvalidOperationException("Only a positive amount can be credited");
                if (string.IsNullOrWhiteSpace(request.GroupNumber))
                    throw new ArgumentException("GroupNumber is required to credit a sponsor account");
                var group = request.GroupNumber.Trim();
                await CreditAsync(group, item.Amount, SponsorAccountEntryType.ExceptionCredit,
                    item.BatchId, item.TraceNumber, null, item.Reference, $"Exception {item.Id}: {Sanitize(request.Note)}");
                await SponsorAccountBalances.RefreshAsync(_invoices, _accounts, group, item.PaymentDate);
                item.Status = RemittanceExceptionStatus.Credited;
                item.CreditedGroupNumber = group;
                break;
            }
            case RemittanceExceptionAction.Dismiss:
                item.Status = RemittanceExceptionStatus.Dismissed;
                break;
            default:
                throw new ArgumentException($"Unknown action {request.Action}");
        }

        item.ResolvedAt = DateTime.UtcNow;
        item.ResolvedBy = _actor.UserId;
        item.ResolutionNote = request.Note.Length > 1000 ? request.Note[..1000] : request.Note;
        _logger.LogInformation("Remittance exception {ExceptionId} resolved as {Status} by {Actor}",
            item.Id, item.Status, Sanitize(_actor.UserId));
        return await _exceptions.UpdateAsync(item);
    }

    private async Task<RemittanceApplication> ApplyItemAsync(RemittanceBatch batch, RemittanceItem item, HashSet<string> touchedGroups)
    {
        var reference = item.Reference?.Trim();
        if (item.Amount <= 0)
            return ExceptionRow(item, (await QueueAsync(batch, item, item.Amount, RemittanceExceptionReason.NonPositiveAmount,
                $"Amount {item.Amount:0.00} is not a payment (reversal or recoupment)")).Id);
        if (string.IsNullOrEmpty(reference))
            return ExceptionRow(item, (await QueueAsync(batch, item, item.Amount, RemittanceExceptionReason.MissingReference,
                "The item has no invoice reference")).Id);

        var candidates = (await _invoices.GetByInvoiceNumberAsync(reference)).ToList();
        if (candidates.Count == 0 && !string.Equals(reference, reference.ToUpperInvariant(), StringComparison.Ordinal))
            candidates = (await _invoices.GetByInvoiceNumberAsync(reference.ToUpperInvariant())).ToList();
        var open = candidates.Where(i => i.Status is not (InvoiceStatus.Voided or InvoiceStatus.WriteOff)).ToList();

        if (candidates.Count == 0)
            return ExceptionRow(item, (await QueueAsync(batch, item, item.Amount, RemittanceExceptionReason.InvoiceNotFound,
                $"No invoice numbered {reference}")).Id);
        if (open.Count == 0)
            return ExceptionRow(item, (await QueueAsync(batch, item, item.Amount, RemittanceExceptionReason.InvoiceClosed,
                $"Invoice {reference} is {candidates[0].Status}")).Id);
        if (open.Count > 1)
            return ExceptionRow(item, (await QueueAsync(batch, item, item.Amount, RemittanceExceptionReason.AmbiguousReference,
                $"{open.Count} open invoices are numbered {reference}")).Id);

        var invoice = open[0];
        var posting = PostToInvoice(invoice, item.Amount, batch.PaymentDate, batch.TraceNumber, batch.Id, item.LineNumber, item.MemberId);
        if (posting.Applied > 0)
            await _invoices.UpdateAsync(invoice);
        if (posting.Excess > 0)
            await CreditAsync(invoice.GroupNumber, posting.Excess, SponsorAccountEntryType.OverpaymentCredit,
                batch.Id, batch.TraceNumber, invoice.Id, reference, $"Overpayment of invoice {invoice.InvoiceNumber}");
        touchedGroups.Add(invoice.GroupNumber);

        _logger.LogInformation(
            "Applied {Amount:0.00} from trace {Trace} line {Line} to invoice {InvoiceNumber}: applied {Applied:0.00}, credit {Excess:0.00}, balance {Balance:0.00}",
            item.Amount, Sanitize(batch.TraceNumber), item.LineNumber, invoice.InvoiceNumber, posting.Applied, posting.Excess, invoice.BalanceDue);

        return new RemittanceApplication
        {
            LineNumber = item.LineNumber,
            Level = item.Level,
            Reference = reference,
            MemberId = item.MemberId,
            PaidAmount = item.Amount,
            InvoiceId = invoice.Id,
            InvoiceNumber = invoice.InvoiceNumber,
            GroupNumber = invoice.GroupNumber,
            BalanceBefore = posting.BalanceBefore,
            AppliedAmount = posting.Applied,
            UnappliedCreditAmount = posting.Excess,
            PaymentId = posting.PaymentId,
            Outcome = posting.Excess > 0 ? CashApplicationOutcome.Overpayment
                : posting.Applied == posting.BalanceBefore ? CashApplicationOutcome.ExactMatch
                : CashApplicationOutcome.PartialPayment
        };
    }

    internal readonly record struct Posting(decimal BalanceBefore, decimal Applied, decimal Excess, string? PaymentId);

    /// <summary>
    /// Applies up to the invoice's balance as a payment, recalculates its totals
    /// and status, and returns the excess (to be held as unapplied credit).
    /// </summary>
    internal Posting PostToInvoice(PremiumInvoice invoice, decimal amount, DateTime paymentDate, string trace,
        string batchId, int line, string? memberId)
    {
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
                RecordedBy = _actor.UserId,
                RemittanceBatchId = batchId,
                RemittanceLine = line,
                MemberId = memberId
            };
            invoice.Payments.Add(payment);
            invoice.RecalculateTotals();
            PremiumInvoiceStatus.ApplyPaymentStatus(invoice);
            invoice.LastUpdatedBy = _actor.UserId;
            paymentId = payment.PaymentId;
        }

        return new Posting(balanceBefore, applied, excess, paymentId);
    }

    private Task<SponsorAccount> CreditAsync(string groupNumber, decimal amount, SponsorAccountEntryType type,
        string? batchId, string? trace, string? invoiceId, string? reference, string memo)
    {
        var postedBy = _actor.UserId;
        return _accounts.UpdateAsync(groupNumber, account =>
        {
            account.UnappliedCredit += amount;
            account.NetBalance = account.OpenInvoiceBalance - account.UnappliedCredit;
            account.Entries.Add(new SponsorAccountEntry
            {
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

    private async Task<RemittanceException> QueueAsync(RemittanceBatch batch, RemittanceItem? item, decimal amount,
        RemittanceExceptionReason reason, string detail)
    {
        var exception = await _exceptions.CreateAsync(new RemittanceException
        {
            BatchId = batch.Id,
            Source = batch.Source,
            TraceNumber = batch.TraceNumber,
            PayerId = batch.PayerId,
            PayerName = batch.PayerName,
            PaymentDate = batch.PaymentDate,
            LineNumber = item?.LineNumber ?? 0,
            Reference = item?.Reference?.Trim(),
            MemberId = item?.MemberId,
            Amount = amount,
            Reason = reason,
            Detail = detail
        });
        batch.ExceptionCount++;
        batch.ExceptionAmount += amount;
        _logger.LogWarning("Remittance trace {Trace} line {Line}: {Amount:0.00} queued as {Reason}",
            Sanitize(batch.TraceNumber), item?.LineNumber ?? 0, amount, reason);
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
            .Where(i => i.Status is not (InvoiceStatus.Voided or InvoiceStatus.WriteOff) && i.BalanceDue > 0)
            .Sum(i => i.BalanceDue);
        return await accounts.UpdateAsync(groupNumber, account =>
        {
            account.OpenInvoiceBalance = open;
            account.NetBalance = open - account.UnappliedCredit;
            if (lastPaymentAt.HasValue && (account.LastPaymentAt == null || lastPaymentAt > account.LastPaymentAt))
                account.LastPaymentAt = lastPaymentAt;
        });
    }
}
