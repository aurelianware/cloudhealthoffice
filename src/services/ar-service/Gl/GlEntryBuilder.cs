using System.Text.Json;
using CloudHealthOffice.Finance.Contracts;

namespace ArService.Gl;

/// <summary>A journal entry before accounts are resolved: roles and amounts.</summary>
public sealed class GlProposedEntry
{
    public required GlEntryKind Kind { get; init; }

    /// <summary>Business key within the tenant (one entry per kind per source document).</summary>
    public required string Key { get; init; }
    public required string SourceDocumentId { get; init; }
    public required string SourceReference { get; init; }
    public required DateTime EntryDate { get; init; }
    public string? LineOfBusiness { get; init; }
    public required string Description { get; init; }
    public required string PostedBy { get; init; }

    /// <summary>The amount the source event claims (run net total, file credit total), for reconciliation.</summary>
    public required decimal ClaimedAmount { get; init; }
    public required IReadOnlyList<(GlPostingRole Role, decimal Debit, decimal Credit, string Memo)> Lines { get; init; }
}

/// <summary>The payload cannot be posted: amounts negative or not money, totals inconsistent.</summary>
public sealed class GlInvalidPayloadException : Exception
{
    public GlInvalidPayloadException(string message) : base(message) { }
}

/// <summary>
/// Turns a source event into its entry, purely (no I/O). Null when there is nothing to
/// post (every amount zero). Throws <see cref="GlInvalidPayloadException"/> for a payload
/// that does not add up: an entry is never forced to balance.
/// </summary>
public static class GlEntryBuilder
{
    public static GlProposedEntry? Build(GlEventEnvelope envelope)
    {
        try
        {
            return envelope.Type switch
            {
                GlEventTypes.PaymentRunExecuted => Accrual(Read<PaymentRunExecutedEvent>(envelope)),
                GlEventTypes.ReversalRunExecuted => Recoupment(Read<ReversalRunExecutedEvent>(envelope)),
                GlEventTypes.PaymentFileTransmitted => Transmission(Read<PaymentFileTransmittedEvent>(envelope)),
                _ => throw new GlInvalidPayloadException($"Unknown GL event type '{envelope.Type}'."),
            };
        }
        catch (JsonException ex)
        {
            throw new GlInvalidPayloadException($"The {envelope.Type} payload is not valid JSON for its type ({ex.GetType().Name}).");
        }
    }

    private static T Read<T>(GlEventEnvelope envelope)
        => JsonSerializer.Deserialize<T>(envelope.PayloadJson, GlEventTypes.Json)
           ?? throw new GlInvalidPayloadException($"The {envelope.Type} payload is empty.");

    private static GlProposedEntry? Accrual(PaymentRunExecutedEvent e)
    {
        Require(!string.IsNullOrEmpty(e.PaymentRunId), "PaymentRunExecuted has no paymentRunId.");
        foreach (var p in e.Payments)
        {
            Money(p.NetAmount, $"payment {p.CheckNumber} net amount");
            Money(p.ReceivableOffsetAmount, $"payment {p.CheckNumber} receivable offset");
        }
        var net = e.Payments.Sum(p => p.NetAmount);
        var offsets = e.Payments.Sum(p => p.ReceivableOffsetAmount);
        Require(net == e.TotalNetAmount, $"PaymentRunExecuted totalNetAmount {e.TotalNetAmount:F2} is not the sum of its payments ({net:F2}).");
        Require(offsets == e.TotalReceivableOffsetAmount,
            $"PaymentRunExecuted totalReceivableOffsetAmount {e.TotalReceivableOffsetAmount:F2} is not the sum of its payments ({offsets:F2}).");
        var gross = net + offsets;
        if (gross == 0m)
            return null;

        var lines = new List<(GlPostingRole, decimal, decimal, string)>
        {
            (GlPostingRole.ClaimsExpense, gross, 0m, $"claims paid by payment run {e.PaymentRunNumber} ({e.Payments.Count} payments)"),
        };
        if (net > 0m)
            lines.Add((GlPostingRole.ClaimsPayable, 0m, net, $"payable to providers, payment run {e.PaymentRunNumber}"));
        if (offsets > 0m)
            lines.Add((GlPostingRole.ProviderReceivable, 0m, offsets, $"provider receivables recovered by payment run {e.PaymentRunNumber}"));
        return new GlProposedEntry
        {
            Kind = GlEntryKind.ClaimsAccrual,
            Key = $"accrual:{e.PaymentRunId}",
            SourceDocumentId = e.PaymentRunId,
            SourceReference = e.PaymentRunNumber,
            EntryDate = e.ExecutedAt.Date,
            LineOfBusiness = e.LineOfBusiness,
            Description = $"Claims accrual, payment run {e.PaymentRunNumber} ({e.RunStatus})",
            PostedBy = e.ExecutedBy,
            ClaimedAmount = net,
            Lines = lines,
        };
    }

    private static GlProposedEntry? Recoupment(ReversalRunExecutedEvent e)
    {
        Require(!string.IsNullOrEmpty(e.ReversalRunId), "ReversalRunExecuted has no reversalRunId.");
        foreach (var r in e.Reversals)
            Money(r.Amount, $"reversal {r.CheckNumber} amount");
        var total = e.Reversals.Sum(r => r.Amount);
        Require(total == e.TotalAmount, $"ReversalRunExecuted totalAmount {e.TotalAmount:F2} is not the sum of its reversals ({total:F2}).");
        if (total == 0m)
            return null;
        return new GlProposedEntry
        {
            Kind = GlEntryKind.ClaimsRecoupment,
            Key = $"recoupment:{e.ReversalRunId}",
            SourceDocumentId = e.ReversalRunId,
            SourceReference = e.ReversalRunNumber,
            EntryDate = e.ExecutedAt.Date,
            Description = $"Claims recoupment, reversal run {e.ReversalRunNumber} ({e.RunStatus})",
            PostedBy = e.ExecutedBy,
            ClaimedAmount = total,
            Lines =
            [
                (GlPostingRole.ProviderReceivable, total, 0m, $"owed back by providers, reversal run {e.ReversalRunNumber}"),
                (GlPostingRole.ClaimsExpense, 0m, total, $"claims expense reversed, reversal run {e.ReversalRunNumber} ({e.Reversals.Count} reversals)"),
            ],
        };
    }

    private static GlProposedEntry? Transmission(PaymentFileTransmittedEvent e)
    {
        Require(!string.IsNullOrEmpty(e.PaymentRunId), "PaymentFileTransmitted has no paymentRunId.");
        Money(e.TotalCreditAmount, "file credit total");
        Require(e.TotalDebitAmount == 0m, $"PaymentFileTransmitted carries debits ({e.TotalDebitAmount:F2}); a claims payment file is credits only.");
        if (e.TotalCreditAmount == 0m)
            return null;
        return new GlProposedEntry
        {
            Kind = GlEntryKind.AchTransmission,
            // One per payment run, not per file: a re-dated (superseded) file can never post a second time.
            Key = $"ach:{e.PaymentRunId}",
            SourceDocumentId = e.PaymentRunId,
            SourceReference = e.FileReference,
            EntryDate = e.TransmittedAt.Date,
            Description = $"ACH credits sent to the bank, payment run {e.PaymentRunNumber}, file {e.FileReference} (effective {e.EffectiveEntryDate:yyyy-MM-dd})",
            PostedBy = e.ApprovedBy,
            ClaimedAmount = e.TotalCreditAmount,
            Lines =
            [
                (GlPostingRole.ClaimsPayable, e.TotalCreditAmount, 0m, $"payable settled by file {e.FileReference} ({e.EntryCount} credits)"),
                (GlPostingRole.AchInTransit, 0m, e.TotalCreditAmount, $"ACH in transit, file {e.FileReference}, sha256 {e.Sha256}"),
            ],
        };
    }

    private static void Money(decimal amount, string what)
    {
        if (amount < 0m)
            throw new GlInvalidPayloadException($"The {what} is negative ({amount}).");
        if (decimal.Round(amount, 2) != amount)
            throw new GlInvalidPayloadException($"The {what} is not a money amount ({amount}).");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new GlInvalidPayloadException(message);
    }
}

/// <summary>Journal invariants, enforced before every insert.</summary>
public static class GlJournal
{
    public static string KeyFor(string tenantId, string key) => $"{tenantId}:{key}";

    public static string PeriodOf(DateTime date) => date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Throws unless the entry is balanced double entry: at least two lines, each with
    /// exactly one positive side, money amounts, debits equal credits, totals stored.
    /// </summary>
    public static void EnsureBalanced(GlJournalEntry entry)
    {
        if (entry.Lines.Count < 2)
            throw new InvalidOperationException($"GL entry {entry.Id} has {entry.Lines.Count} line(s); double entry needs at least two.");
        foreach (var line in entry.Lines)
        {
            if (line.Debit < 0m || line.Credit < 0m)
                throw new InvalidOperationException($"GL entry {entry.Id} line {line.LineNumber} has a negative amount.");
            if ((line.Debit > 0m) == (line.Credit > 0m))
                throw new InvalidOperationException($"GL entry {entry.Id} line {line.LineNumber} must have exactly one of debit or credit.");
            if (decimal.Round(line.Debit, 2) != line.Debit || decimal.Round(line.Credit, 2) != line.Credit)
                throw new InvalidOperationException($"GL entry {entry.Id} line {line.LineNumber} is not a money amount.");
            if (string.IsNullOrEmpty(line.AccountId) || string.IsNullOrEmpty(line.AccountNumber))
                throw new InvalidOperationException($"GL entry {entry.Id} line {line.LineNumber} has no account.");
        }
        var debits = entry.Lines.Sum(l => l.Debit);
        var credits = entry.Lines.Sum(l => l.Credit);
        if (debits != credits)
            throw new InvalidOperationException($"GL entry {entry.Id} does not balance: debits {debits:F2}, credits {credits:F2}.");
        if (entry.TotalDebit != debits || entry.TotalCredit != credits)
            throw new InvalidOperationException($"GL entry {entry.Id} totals do not match its lines.");
    }
}
