using CloudHealthOffice.Finance.Contracts;

namespace ArService.Gl;

/// <summary>A difference the reconciliation found.</summary>
public enum GlReconciliationFlag
{
    /// <summary>The run's accrual event is parked (not in the GL).</summary>
    AccrualParked,

    /// <summary>A file was transmitted for a run whose accrual never reached the GL.</summary>
    AccrualMissing,

    /// <summary>The accrual's payable credit differs from the run's net total.</summary>
    AccrualAmountMismatch,

    /// <summary>The transmission event is parked (not in the GL).</summary>
    TransmissionParked,

    /// <summary>The transmission entry's payable debit differs from the file's credit total.</summary>
    TransmissionAmountMismatch,

    /// <summary>More than one transmitted file reached the GL for one run (only one posts).</summary>
    DuplicateTransmission,

    /// <summary>More was sent to the bank than the run accrued.</summary>
    TransmittedMoreThanAccrued,

    /// <summary>The run's ACH payments add up to more or less than the file sent: a payee moved to check after execution.</summary>
    FileDiffersFromAchPayments,

    /// <summary>Some of the run's payable has not been sent (checks, or the file not transmitted yet). Informational.</summary>
    PayableOutstanding,

    /// <summary>An entry of the run was reversed by an operator.</summary>
    EntryReversed,
}

public sealed class GlRunReconciliation
{
    public string PaymentRunId { get; init; } = string.Empty;
    public string PaymentRunNumber { get; init; } = string.Empty;

    /// <summary>Net payable the run issued (all methods), per its event.</summary>
    public decimal RunNetTotal { get; init; }

    /// <summary>Of which ACH (expected in the file).</summary>
    public decimal RunAchTotal { get; init; }
    public decimal RunCheckTotal { get; init; }
    public decimal AccruedPayable { get; init; }
    public decimal FileCreditTotal { get; init; }
    public decimal TransmittedPayable { get; init; }

    /// <summary>Accrued minus transmitted (after reversals of either entry).</summary>
    public decimal OutstandingPayable { get; init; }
    public List<GlReconciliationFlag> Flags { get; init; } = new();
}

public sealed class GlReconciliationReport
{
    public string TenantId { get; init; } = string.Empty;
    public string? Period { get; init; }
    public List<GlRunReconciliation> Runs { get; init; } = new();

    /// <summary>The claims payable balance in the GL (credits minus debits on ClaimsPayable lines, every entry).</summary>
    public decimal GlClaimsPayableBalance { get; init; }

    /// <summary>The sum of the runs' outstanding payable.</summary>
    public decimal RunsOutstandingPayable { get; init; }

    /// <summary><see cref="GlClaimsPayableBalance"/> minus <see cref="RunsOutstandingPayable"/>: anything but zero needs a person.</summary>
    public decimal Difference { get; init; }
    public int ParkedEventCount { get; init; }
    public bool Balanced => Difference == 0m && Runs.All(r => r.Flags.All(f => f is GlReconciliationFlag.PayableOutstanding));
}

/// <summary>
/// GL claims payable against the runs and the transmitted files, from the GL's own
/// register of source events (each carries its run's or file's totals) and the journal.
/// <paramref name="period"/> limits the runs to those whose accrual is dated in it; the
/// GL balance is always the whole ledger's.
/// </summary>
public static class GlReconciliationBuilder
{
    public static GlReconciliationReport Build(string tenantId, string? period, IReadOnlyList<GlSourceEvent> events, IReadOnlyList<GlJournalEntry> journal)
    {
        var byId = journal.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var reversed = journal.Where(e => e.ReversesEntryId != null).Select(e => e.ReversesEntryId!).ToHashSet(StringComparer.Ordinal);

        decimal Payable(GlJournalEntry? entry, bool credit)
        {
            if (entry == null) return 0m;
            var amount = entry.Lines.Where(l => l.Role == GlPostingRole.ClaimsPayable).Sum(l => credit ? l.Credit : l.Debit);
            return reversed.Contains(entry.Id) ? 0m : amount;
        }

        var runIds = events
            .Where(e => e.Type is GlEventTypes.PaymentRunExecuted or GlEventTypes.PaymentFileTransmitted && !string.IsNullOrEmpty(e.SourceDocumentId))
            .Select(e => e.SourceDocumentId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);

        var runs = new List<GlRunReconciliation>();
        foreach (var runId in runIds)
        {
            var accrualEvent = events.FirstOrDefault(e => e.Type == GlEventTypes.PaymentRunExecuted && e.SourceDocumentId == runId);
            var fileEvents = events.Where(e => e.Type == GlEventTypes.PaymentFileTransmitted && e.SourceDocumentId == runId).ToList();
            var accrualEntry = byId.GetValueOrDefault(GlJournal.KeyFor(tenantId, $"accrual:{runId}"));
            var achEntry = byId.GetValueOrDefault(GlJournal.KeyFor(tenantId, $"ach:{runId}"));
            // With a period: runs whose accrual is dated in it, plus runs whose accrual has not
            // posted (parked or missing) so they are never hidden by the filter.
            if (period != null && accrualEntry != null && accrualEntry.Period != period)
                continue;

            var payload = accrualEvent == null ? null
                : System.Text.Json.JsonSerializer.Deserialize<PaymentRunExecutedEvent>(accrualEvent.PayloadJson, GlEventTypes.Json);
            var runNet = payload?.TotalNetAmount ?? 0m;
            var ach = payload?.Payments.Where(p => string.Equals(p.PaymentMethod, "ACH", StringComparison.OrdinalIgnoreCase)).Sum(p => p.NetAmount) ?? 0m;
            var fileTotal = fileEvents.Where(e => e.Status != GlSourceEventStatus.Dismissed).Select(e => e.ClaimedAmount).FirstOrDefault();
            var accrued = Payable(accrualEntry, credit: true);
            var transmitted = Payable(achEntry, credit: false);

            var flags = new List<GlReconciliationFlag>();
            if (accrualEvent == null) flags.Add(GlReconciliationFlag.AccrualMissing);
            else if (accrualEvent.Status == GlSourceEventStatus.Parked) flags.Add(GlReconciliationFlag.AccrualParked);
            else if (accrualEntry != null && !reversed.Contains(accrualEntry.Id) && accrued != runNet) flags.Add(GlReconciliationFlag.AccrualAmountMismatch);
            if (fileEvents.Any(e => e.Status == GlSourceEventStatus.Parked && e.ParkReason != GlParkReason.DuplicateBusinessKey))
                flags.Add(GlReconciliationFlag.TransmissionParked);
            if (fileEvents.Count(e => e.Status != GlSourceEventStatus.Dismissed) > 1) flags.Add(GlReconciliationFlag.DuplicateTransmission);
            if (achEntry != null && fileEvents.FirstOrDefault(e => e.EntryId == achEntry.Id) is { } posted && posted.ClaimedAmount != transmitted && !reversed.Contains(achEntry.Id))
                flags.Add(GlReconciliationFlag.TransmissionAmountMismatch);
            if (transmitted > accrued) flags.Add(GlReconciliationFlag.TransmittedMoreThanAccrued);
            if (fileEvents.Count > 0 && payload != null && fileTotal != ach) flags.Add(GlReconciliationFlag.FileDiffersFromAchPayments);
            if (accrued - transmitted > 0m) flags.Add(GlReconciliationFlag.PayableOutstanding);
            if ((accrualEntry != null && reversed.Contains(accrualEntry.Id)) || (achEntry != null && reversed.Contains(achEntry.Id)))
                flags.Add(GlReconciliationFlag.EntryReversed);

            runs.Add(new GlRunReconciliation
            {
                PaymentRunId = runId,
                PaymentRunNumber = payload?.PaymentRunNumber ?? fileEvents.Select(e => e.SourceReference).FirstOrDefault() ?? string.Empty,
                RunNetTotal = runNet,
                RunAchTotal = ach,
                RunCheckTotal = runNet - ach,
                AccruedPayable = accrued,
                FileCreditTotal = fileTotal,
                TransmittedPayable = transmitted,
                OutstandingPayable = accrued - transmitted,
                Flags = flags.Distinct().ToList(),
            });
        }

        var glPayable = journal.SelectMany(e => e.Lines).Where(l => l.Role == GlPostingRole.ClaimsPayable).Sum(l => l.Credit - l.Debit);
        var allRunsOutstanding = period == null ? runs.Sum(r => r.OutstandingPayable)
            : Build(tenantId, null, events, journal).RunsOutstandingPayable;
        return new GlReconciliationReport
        {
            TenantId = tenantId,
            Period = period,
            Runs = runs,
            GlClaimsPayableBalance = glPayable,
            RunsOutstandingPayable = allRunsOutstanding,
            Difference = glPayable - allRunsOutstanding,
            ParkedEventCount = events.Count(e => e.Status == GlSourceEventStatus.Parked),
        };
    }
}
