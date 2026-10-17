using CloudHealthOffice.BankReconciliation;
using CloudHealthOffice.BankReconciliation.Bai2;
using Microsoft.Extensions.Options;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;
using CloudHealthOffice.Infrastructure.Logging;

namespace PremiumBillingService.BankReconciliation;

/// <summary>Configuration section <c>BankReconciliation:Premium</c>.</summary>
public sealed class PremiumBankReconciliationOptions
{
    public const string SectionName = "BankReconciliation:Premium";

    /// <summary>BAI2 credit type codes that are premium cash.</summary>
    public List<string> CashCreditTypeCodes { get; set; } = [.. Bai2TypeCodes.DefaultPremiumCashCredits];

    /// <summary>Lockbox deposit totals, matched to the day's lockbox batches.</summary>
    public List<string> LockboxDepositTypeCodes { get; set; } = [.. Bai2TypeCodes.DefaultLockboxDeposits];

    /// <summary>ACH returns (returned premium drafts, returned items): always flagged.</summary>
    public List<string> AchReturnTypeCodes { get; set; } = [.. Bai2TypeCodes.DefaultAchReturns];

    /// <summary>
    /// Apply a credit straight to an invoice when its references name exactly
    /// one open invoice and the amount is exactly that invoice's balance. Off,
    /// such lines go to suspense like any other unmatched credit.
    /// </summary>
    public bool AutoApplyExactInvoiceMatch { get; set; } = true;

    /// <summary>Days either side of the bank date that a recorded payment's date may fall on.</summary>
    public int DateToleranceDays { get; set; } = 1;

    /// <summary>
    /// Days either side of the bank date within which a payment already on
    /// the invoice for the same amount (from an 820 or lockbox) makes an exact
    /// match a possible duplicate (suspense) instead of being applied. Default
    /// 7 calendar days, about 5 banking days, so a weekend or a reference the
    /// bank formats differently does not post the same money twice.
    /// </summary>
    public int PossibleDuplicateWindowDays { get; set; } = CashApplicationService.BankDuplicateWindowDays;

    /// <summary>
    /// What an invoice number looks like. Only reference tokens matching it in
    /// full are looked up as invoice numbers (case-insensitive). Default:
    /// <c>INV-</c> followed by letters, digits and hyphens (INV-{group}-{yyyy-MM}).
    /// </summary>
    public string InvoiceNumberPattern { get; set; } = "^INV-[A-Z0-9][A-Z0-9-]{2,60}$";
}

/// <summary>
/// Premium cash rules for a bank credit, strongest first:
/// <list type="number">
/// <item><b>Already recorded.</b> A remittance (820 or lockbox) with a reference
/// the line carries, the same amount, and a payment date within the tolerance:
/// the bank confirms that cash. Nothing is posted again.</item>
/// <item><b>Lockbox deposit total</b> (115): the lockbox batches of the
/// as-of date (of the lockbox the line names, if it names one) add up exactly
/// to the deposit.</item>
/// <item><b>Exact invoice.</b> The references name exactly one open invoice,
/// and the amount is exactly its balance: applied through
/// <see cref="ICashApplicationService"/>, the same matching and posting as an
/// 820, as source <see cref="RemittanceSource.BankStatement"/>.</item>
/// <item>Anything else goes to suspense with its candidates: amount-only
/// matches, partial amounts and several candidates are never applied.</item>
/// </list>
/// ACH return codes are always flagged for review.
/// </summary>
public sealed class PremiumBankTransactionMatcher : IBankTransactionMatcher
{
    public const string BankPayerId = "BANK";

    private readonly ICashApplicationService _cash;
    private readonly IRemittanceBatchRepository _batches;
    private readonly IPremiumInvoiceRepository _invoices;
    private readonly PremiumBankReconciliationOptions _options;
    private readonly ILogger<PremiumBankTransactionMatcher> _logger;
    private readonly BankStatementFreeze _freeze;

    public PremiumBankTransactionMatcher(
        ICashApplicationService cash,
        IRemittanceBatchRepository batches,
        IPremiumInvoiceRepository invoices,
        IOptions<PremiumBankReconciliationOptions> options,
        ILogger<PremiumBankTransactionMatcher> logger,
        BankStatementFreeze freeze)
    {
        _freeze = freeze;
        _cash = cash;
        _batches = batches;
        _invoices = invoices;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MatchDecision> DecideAsync(BankTransaction tx, CancellationToken ct = default)
    {
        if (_options.AchReturnTypeCodes.Contains(tx.TypeCode))
            return new MatchDecision("AchReturn", ReconciliationStatus.Return,
                $"ACH return (type {tx.TypeCode}): find the payment it reverses and reverse or re-bill it.");

        if (tx.Direction != Bai2Direction.Credit || !_options.CashCreditTypeCodes.Contains(tx.TypeCode))
            return MatchDecision.Ignore($"Type {tx.TypeCode} is not premium cash.");

        var tokens = BankReferenceTokens.From(tx);

        if (_options.LockboxDepositTypeCodes.Contains(tx.TypeCode))
            return await LockboxDepositAsync(tx, tokens);

        // An earlier run may have applied this line and stopped before recording
        // the decision: its batch id is derived from the line, so look it up first.
        var batchId = RemittanceBatch.IdFor(tx.TenantId, RemittanceSource.BankStatement, BankPayerId, TraceFor(tx));
        if (await _batches.GetByIdAsync(batchId) is { } alreadyPosted)
            return Recorded(alreadyPosted);

        // 1. Money already recorded from an 820 or lockbox file.
        var recorded = await RecordedRemittancesAsync(tx, tokens);
        if (recorded.Count == 1)
            return new MatchDecision("PremiumCash", ReconciliationStatus.Matched,
                $"Confirms recorded {recorded[0].Source} payment {recorded[0].TraceNumber}.",
                "RemittanceBatch", [recorded[0].Id]);
        if (recorded.Count > 1)
            return Suspense("Several recorded payments carry this reference and amount.", recorded.Select(b => $"RemittanceBatch:{b.Id}"));

        // 2. One open invoice, for exactly its balance.
        var open = await OpenInvoicesNamedAsync(tokens);
        if (open.Count == 0)
            return Suspense(tokens.Count == 0
                ? "The bank line carries no reference to match."
                : "No recorded payment or open invoice matches the bank line's references.", []);

        var candidates = open.Select(i => $"PremiumInvoice:{i.Id}:{i.InvoiceNumber}:{i.BalanceDue:0.00}");
        if (open.Count > 1)
            return Suspense("The references name several open invoices.", candidates);

        var invoice = open[0];
        if (invoice.BalanceDue != tx.Amount)
            return Suspense($"Invoice {invoice.InvoiceNumber} is open for {invoice.BalanceDue:0.00}, the bank line is {tx.Amount:0.00}.", candidates);
        if (!_options.AutoApplyExactInvoiceMatch)
            return Suspense($"Exact match to invoice {invoice.InvoiceNumber}; automatic application is off.", candidates);

        // The 820 or lockbox payment for this money may already be on the invoice
        // under a reference or date the bank reports differently: then the
        // remaining balance equals the bank amount by coincidence of the split.
        var window = TimeSpan.FromDays(_options.PossibleDuplicateWindowDays);
        var sameMoney = invoice.Payments.FirstOrDefault(p =>
            p.Amount == tx.Amount && !CashApplicationService.IsBankStatementPayment(p) && (p.PaymentDate - tx.AsOfDate).Duration() <= window);
        if (sameMoney is not null)
            return new MatchDecision("PremiumCash", ReconciliationStatus.Suspense,
                $"PossibleDuplicate: invoice {invoice.InvoiceNumber} already has a {sameMoney.Amount:0.00} payment dated {sameMoney.PaymentDate:yyyy-MM-dd} " +
                $"(reference {sameMoney.ReferenceNumber}) within {_options.PossibleDuplicateWindowDays} days of this bank line; it may be the same money. Not applied.",
                Candidates: [$"PremiumInvoice:{invoice.Id}:{invoice.InvoiceNumber}:{invoice.BalanceDue:0.00}", $"InvoicePayment:{sameMoney.PaymentId}:{sameMoney.ReferenceNumber}"]);

        return await ApplyAsync(tx, invoice);
    }

    internal static string TraceFor(BankTransaction tx) => $"{CashApplicationService.BankStatementTracePrefix}{tx.Id}";

    private MatchDecision Recorded(RemittanceBatch batch)
    {
        var application = batch.Applications.FirstOrDefault();
        if (application?.Outcome is CashApplicationOutcome.ExactMatch or CashApplicationOutcome.PartialPayment or CashApplicationOutcome.Overpayment)
            return new MatchDecision("PremiumCash", ReconciliationStatus.Applied,
                $"Applied to invoice {application.InvoiceNumber} ({application.Outcome}).", "RemittanceBatch", [batch.Id]);

        // Cash application held it (e.g. a possible duplicate of an 820): it is in that exceptions queue now.
        _logger.LogWarning("Bank line batch {BatchId} was recorded but cash application queued it as an exception", LogSanitizer.Sanitize(batch.Id));
        return new MatchDecision("PremiumCash", ReconciliationStatus.Matched,
            $"Recorded through cash application, which put it in the remittance exceptions queue ({string.Join(", ", batch.Exceptions.Select(e => e.Id))}).",
            "RemittanceBatch", [batch.Id]);
    }

    /// <summary>Applies the bank line to <paramref name="invoice"/> through cash application (also used to resolve a suspense item).</summary>
    public async Task<MatchDecision> ApplyAsync(BankTransaction tx, PremiumInvoice invoice, decimal? amount = null)
    {
        // Commit point: a supersession requested since the run started stops the posting.
        await _freeze.EnsureNotFrozenAsync(tx.TenantId, tx.StatementId);
        var advice = ToAdvice(tx, invoice.InvoiceNumber, amount ?? tx.Amount);
        RemittanceBatch batch;
        try
        {
            batch = await _cash.ApplyAsync(advice);
        }
        catch (DuplicateRemittanceException ex)
        {
            // An earlier run applied it and stopped before recording the decision.
            return await _batches.GetByIdAsync(ex.BatchId) is { } earlier
                ? Recorded(earlier)
                : new MatchDecision("PremiumCash", ReconciliationStatus.Applied,
                    $"Applied to invoice {invoice.InvoiceNumber} (recorded earlier).", "RemittanceBatch", [ex.BatchId]);
        }
        return Recorded(batch);
    }

    internal static RemittanceAdvice ToAdvice(BankTransaction tx, string invoiceNumber, decimal amount) => new()
    {
        Source = RemittanceSource.BankStatement,
        SourceFileName = $"bai2:{tx.StatementId}",
        // The batch id derives from this: one bank line is one payment, posted once.
        TraceNumber = TraceFor(tx),
        PayerId = BankPayerId,
        PaymentMethod = tx.TypeCode switch
        {
            "142" or "165" or "166" or "169" => "ACH",
            "195" or "208" => "FWT",
            _ => "BOP",
        },
        // The bank's customer reference is usually the payer's trace (TRN02) or check
        // number: an 820 for the same money arriving later then has the same
        // cross-source key and is held as a possible duplicate instead of posting twice.
        CheckNumber = tx.CustomerReference,
        PaymentAmount = amount,
        PaymentDate = tx.AsOfDate.Date,
        Items =
        [
            new RemittanceItem
            {
                LineNumber = 1,
                Level = RemittanceLevel.Organization,
                Reference = invoiceNumber,
                Amount = amount,
            },
        ],
    };

    private async Task<MatchDecision> LockboxDepositAsync(BankTransaction tx, IReadOnlyList<string> tokens)
    {
        var date = tx.AsOfDate.Date;
        var batches = new List<RemittanceBatch>();
        for (var page = 1; page <= 20; page++)
        {
            var chunk = (await _batches.SearchAsync(date.AddDays(-3), date.AddDays(10), page, 200)).ToList();
            batches.AddRange(chunk.Where(b => b.Source == RemittanceSource.Lockbox && b.PaymentDate.Date == date && b.Status != RemittanceBatchStatus.NotPosted));
            if (chunk.Count < 200) break;
        }

        // A lockbox number among the references narrows to that lockbox's batches.
        var named = batches.Where(b => tokens.Any(t => b.TraceNumber.StartsWith($"LBX-{t}-", StringComparison.OrdinalIgnoreCase))).ToList();
        if (named.Count > 0) batches = named;

        var total = batches.Sum(b => b.PaymentAmount);
        if (batches.Count > 0 && total == tx.Amount)
            return new MatchDecision("LockboxDeposit", ReconciliationStatus.Matched,
                $"Equals the {batches.Count} lockbox payment(s) recorded for {date:yyyy-MM-dd}.",
                "RemittanceBatch", batches.Select(b => b.Id).ToList());

        return new MatchDecision("LockboxDeposit", ReconciliationStatus.Suspense,
            batches.Count == 0
                ? $"No lockbox payments are recorded for {date:yyyy-MM-dd}: load the lockbox file, then resolve this item."
                : $"The lockbox payments recorded for {date:yyyy-MM-dd} total {total:0.00}; the deposit is {tx.Amount:0.00}.",
            Candidates: batches.Select(b => $"RemittanceBatch:{b.Id}:{b.PaymentAmount:0.00}").ToList());
    }

    private async Task<List<RemittanceBatch>> RecordedRemittancesAsync(BankTransaction tx, IReadOnlyList<string> tokens)
    {
        var dates = Enumerable.Range(-_options.DateToleranceDays, 2 * _options.DateToleranceDays + 1)
            .Select(d => tx.AsOfDate.Date.AddDays(d)).ToList();
        if (tx.ValueDate is { } v && !dates.Contains(v.Date)) dates.Add(v.Date);

        var found = new Dictionary<string, RemittanceBatch>();
        foreach (var token in tokens)
        foreach (var date in dates)
        foreach (var b in await _batches.FindByCrossSourceKeyAsync(RemittanceBatch.CrossSourceKeyFor(token, token, tx.Amount, date)))
        {
            if (b.Source != RemittanceSource.BankStatement && b.Status != RemittanceBatchStatus.NotPosted)
                found[b.Id] = b;
        }
        return found.Values.ToList();
    }

    private async Task<List<PremiumInvoice>> OpenInvoicesNamedAsync(IReadOnlyList<string> tokens)
    {
        var pattern = new System.Text.RegularExpressions.Regex(_options.InvoiceNumberPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        var found = new Dictionary<string, PremiumInvoice>();
        // Only tokens shaped like an invoice number, matched in full, are looked up.
        foreach (var token in tokens.Where(t => pattern.IsMatch(t)))
        foreach (var invoice in await _invoices.GetByInvoiceNumberAsync(token))
        {
            // A Draft (rated, not issued) is never matched or applied.
            if (invoice.BalanceDue > 0 && invoice.Status is not (InvoiceStatus.Paid or InvoiceStatus.Voided or InvoiceStatus.WriteOff or InvoiceStatus.Draft))
                found[invoice.Id] = invoice;
        }
        return found.Values.ToList();
    }

    private static MatchDecision Suspense(string reason, IEnumerable<string> candidates) =>
        new("PremiumCash", ReconciliationStatus.Suspense, reason, Candidates: candidates.ToList());
}
