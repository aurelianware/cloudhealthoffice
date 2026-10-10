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

    /// <summary>More was sent to the bank (or voided) than the run accrued.</summary>
    TransmittedMoreThanAccrued,

    /// <summary>
    /// The file's credit total is not the sum of the run's payments the file names (or names a
    /// payment the run did not accrue). For older events without payment ids: not the sum of
    /// the run's ACH payments.
    /// </summary>
    FileDiffersFromAchPayments,

    /// <summary>Some of the run's payable has not been sent (checks, or the file not transmitted yet). Informational.</summary>
    PayableOutstanding,

    /// <summary>An entry of the run was reversed and nothing replaces it (no repost).</summary>
    EntryReversed,

    /// <summary>A payment-void event of the run is parked (its payable is not relieved in the GL).</summary>
    VoidParked,

    /// <summary>The run was executed before the GL cutover: it is in the opening balances, not here. Informational.</summary>
    PreCutover,
}

public sealed class GlRunReconciliation
{
    public string PaymentRunId { get; init; } = string.Empty;
    public string PaymentRunNumber { get; init; } = string.Empty;

    /// <summary>Net payable the run issued (all methods), per its event.</summary>
    public decimal RunNetTotal { get; init; }

    /// <summary>Of which in the transmitted file (by the file's payment ids; else the ACH payments).</summary>
    public decimal RunAchTotal { get; init; }

    /// <summary>Of which voided before transmission.</summary>
    public decimal RunVoidedTotal { get; init; }

    /// <summary>The rest: checks, check fallbacks, or not sent yet.</summary>
    public decimal RunCheckTotal { get; init; }
    public decimal AccruedPayable { get; init; }
    public decimal FileCreditTotal { get; init; }
    public decimal TransmittedPayable { get; init; }
    public decimal VoidedPayable { get; init; }

    /// <summary>Remainders of excluded payments paid another way (VoidRemainderReissued).</summary>
    public decimal ReissuedPayable { get; init; }

    /// <summary>Accrued minus transmitted minus voided (net of reversals and reposts).</summary>
    public decimal OutstandingPayable { get; init; }
    public List<GlReconciliationFlag> Flags { get; init; } = new();
}

/// <summary>Something finance must post by hand: open until its event is dismissed with the posting named.</summary>
public sealed class GlOpenItem
{
    public string EventId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string PaymentRunId { get; init; } = string.Empty;
    public string Reference { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string? Detail { get; init; }
    public DateTime ReceivedAt { get; init; }
}

public sealed class GlReconciliationReport
{
    /// <summary>
    /// Refunds owed to providers (ProviderRefundOwed) and anything else posted by hand: each is
    /// open until finance posts it and dismisses its event. Any open item keeps the report unbalanced.
    /// </summary>
    public List<GlOpenItem> OpenItems { get; init; } = new();
    public decimal OpenItemsTotal { get; init; }

    public string TenantId { get; init; } = string.Empty;
    public string? Period { get; init; }

    /// <summary>The page of runs (see <see cref="Offset"/>, <see cref="TotalRuns"/>).</summary>
    public List<GlRunReconciliation> Runs { get; init; } = new();
    public int Offset { get; init; }
    public int TotalRuns { get; init; }

    /// <summary>The claims payable balance in the GL (credits minus debits on ClaimsPayable lines, every entry).</summary>
    public decimal GlClaimsPayableBalance { get; init; }

    /// <summary>The sum of every run's outstanding payable (not only this page's).</summary>
    public decimal RunsOutstandingPayable { get; init; }

    /// <summary><see cref="GlClaimsPayableBalance"/> minus <see cref="RunsOutstandingPayable"/>: anything but zero needs a person.</summary>
    public decimal Difference { get; init; }
    public int ParkedEventCount { get; init; }

    /// <summary>Runs (all pages) with a flag that needs a person.</summary>
    public int RunsNeedingAttention { get; init; }
    public bool Balanced => Difference == 0m && RunsNeedingAttention == 0 && OpenItems.Count == 0;

    internal static bool Informational(GlReconciliationFlag flag) => flag is GlReconciliationFlag.PayableOutstanding or GlReconciliationFlag.PreCutover;
}

/// <summary>
/// GL claims payable against the runs and the transmitted files, from the GL's own
/// register of source events (each carries its run's or file's totals) and the journal.
/// <paramref name="period"/> limits the runs to those whose accrual is dated in it; the
/// GL balance is always the whole ledger's. Every entry of a business key counts, with its
/// reversal and reposts (<c>{key}:r{n}</c>), so a reversed-and-reposted entry nets once.
/// </summary>
public static class GlReconciliationBuilder
{
    public static GlReconciliationReport Build(string tenantId, string? period, IReadOnlyList<GlSourceEvent> events, IReadOnlyList<GlJournalEntry> journal,
        int offset = 0, int limit = int.MaxValue)
    {
        var all = BuildRuns(tenantId, events, journal);
        var inPeriod = period == null ? all : all.Where(r => r.Period == null || r.Period == period).ToList();
        var glPayable = journal.SelectMany(e => e.Lines).Where(l => l.Role == GlPostingRole.ClaimsPayable).Sum(l => l.Credit - l.Debit);
        var outstanding = all.Sum(r => r.Run.OutstandingPayable);
        offset = Math.Max(0, offset);
        var openItems = events
            .Where(e => e.Status == GlSourceEventStatus.Parked && e.ParkReason == GlParkReason.ManualPostingRequired)
            .OrderBy(e => e.ReceivedAt).ThenBy(e => e.EventId, StringComparer.Ordinal)
            .Select(e => new GlOpenItem
            {
                EventId = e.EventId, Type = e.Type, PaymentRunId = e.SourceDocumentId, Reference = e.SourceReference,
                Amount = e.ClaimedAmount, Detail = e.ParkDetail, ReceivedAt = e.ReceivedAt,
            })
            .ToList();
        // The unreversed remainder of an excluded payment: still owed to the provider until its
        // reissue posts (VoidRemainderReissued).
        var byId = events.ToDictionary(e => e.EventId, StringComparer.Ordinal);
        foreach (var v in events.Where(e => e.Type == GlEventTypes.PaymentVoidedBeforeTransmission
                                            && e.Status is not GlSourceEventStatus.Dismissed
                                            && e.ParkReason != GlParkReason.PreCutover))
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<PaymentVoidedBeforeTransmissionEvent>(v.PayloadJson, GlEventTypes.Json);
            if (payload is not { UnpaidRemainder: > 0m })
                continue;
            var reissueId = GlEventTypes.EventIdFor(tenantId, $"reissue:{payload.PaymentId}", GlEventTypes.VoidRemainderReissued);
            if (byId.TryGetValue(reissueId, out var reissue) && reissue.Status is GlSourceEventStatus.Posted or GlSourceEventStatus.Dismissed)
                continue;
            openItems.Add(new GlOpenItem
            {
                EventId = v.EventId, Type = "VoidRemainderUnpaid", PaymentRunId = payload.PaymentRunId,
                Reference = $"{payload.PaymentRunNumber}/{payload.CheckNumber}", Amount = payload.UnpaidRemainder, ReceivedAt = v.ReceivedAt,
                Detail = $"{payload.UnpaidRemainder:F2} of payment {payload.CheckNumber} (NPI {payload.PayeeNpi}) was not reversed and is still owed: " +
                         "pay it and record it (POST /api/paymentruns/{id}/eft-file/voided-payments/{paymentId}/reissue-remainder in payment-service)" +
                         (reissue == null ? string.Empty : $"; its reissue event is {reissue.Status} {reissue.ParkReason}"),
            });
        }
        return new GlReconciliationReport
        {
            OpenItems = openItems,
            OpenItemsTotal = openItems.Sum(i => i.Amount),
            TenantId = tenantId,
            Period = period,
            Runs = inPeriod.Skip(offset).Take(Math.Max(0, limit)).Select(r => r.Run).ToList(),
            Offset = offset,
            TotalRuns = inPeriod.Count,
            GlClaimsPayableBalance = glPayable,
            RunsOutstandingPayable = outstanding,
            Difference = glPayable - outstanding,
            ParkedEventCount = events.Count(e => e.Status == GlSourceEventStatus.Parked),
            RunsNeedingAttention = inPeriod.Count(r => r.Run.Flags.Any(f => !GlReconciliationReport.Informational(f))),
        };
    }

    private sealed record RunRow(GlRunReconciliation Run, string? Period);

    private static List<RunRow> BuildRuns(string tenantId, IReadOnlyList<GlSourceEvent> events, IReadOnlyList<GlJournalEntry> journal)
    {
        var reversed = journal.Where(e => e.ReversesEntryId != null).Select(e => e.ReversesEntryId!).ToHashSet(StringComparer.Ordinal);
        var bySource = journal.ToLookup(e => e.SourceDocumentId, StringComparer.Ordinal);

        // Every entry of the key's chain: the key, its reposts, and their reversals.
        List<GlJournalEntry> Chain(string runId, string key)
        {
            var baseId = GlJournal.KeyFor(tenantId, key);
            return bySource[runId].Where(e => e.Id == baseId || e.Id.StartsWith(baseId + ":", StringComparison.Ordinal)).ToList();
        }
        static decimal PayableCredit(IEnumerable<GlJournalEntry> entries)
            => entries.SelectMany(e => e.Lines).Where(l => l.Role == GlPostingRole.ClaimsPayable).Sum(l => l.Credit - l.Debit);
        GlJournalEntry? Live(IEnumerable<GlJournalEntry> chain)
            => chain.Where(e => e.Kind != GlEntryKind.Reversal && !reversed.Contains(e.Id)).OrderBy(e => e.Id, StringComparer.Ordinal).LastOrDefault();

        var runIds = events
            .Where(e => e.Type is GlEventTypes.PaymentRunExecuted or GlEventTypes.PaymentFileTransmitted or GlEventTypes.PaymentVoidedBeforeTransmission
                        && !string.IsNullOrEmpty(e.SourceDocumentId))
            .Select(e => e.SourceDocumentId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);

        var rows = new List<RunRow>();
        foreach (var runId in runIds)
        {
            var accrualEvent = events.FirstOrDefault(e => e.Type == GlEventTypes.PaymentRunExecuted && e.SourceDocumentId == runId);
            var fileEvents = events.Where(e => e.Type == GlEventTypes.PaymentFileTransmitted && e.SourceDocumentId == runId).ToList();
            var voidEvents = events.Where(e => e.Type == GlEventTypes.PaymentVoidedBeforeTransmission && e.SourceDocumentId == runId).ToList();
            var accrualChain = Chain(runId, $"accrual:{runId}");
            var achChain = Chain(runId, $"ach:{runId}");
            var voidChain = bySource[runId].Where(e => e.Id.StartsWith(GlJournal.KeyFor(tenantId, "void:"), StringComparison.Ordinal)).ToList();
            var reissueChain = bySource[runId].Where(e => e.Id.StartsWith(GlJournal.KeyFor(tenantId, "reissue:"), StringComparison.Ordinal)).ToList();
            var accrualLive = Live(accrualChain);
            var achLive = Live(achChain);

            var payload = accrualEvent == null ? null
                : System.Text.Json.JsonSerializer.Deserialize<PaymentRunExecutedEvent>(accrualEvent.PayloadJson, GlEventTypes.Json);
            var runNet = payload?.TotalNetAmount ?? 0m;

            // The file that counts: the one whose entry is live, else the first not dismissed.
            var fileEvent = fileEvents.FirstOrDefault(e => achLive != null && e.EntryId == achLive.Id)
                            ?? fileEvents.FirstOrDefault(e => e.Status != GlSourceEventStatus.Dismissed);
            var filePayload = fileEvent == null ? null
                : System.Text.Json.JsonSerializer.Deserialize<PaymentFileTransmittedEvent>(fileEvent.PayloadJson, GlEventTypes.Json);
            var fileTotal = fileEvent?.ClaimedAmount ?? 0m;
            var byPayment = payload?.Payments.ToDictionary(p => p.PaymentId, StringComparer.Ordinal) ?? new Dictionary<string, GlPaymentLine>();
            bool unknownInFile = false;
            decimal inFile;
            if (filePayload is { PaymentIds.Count: > 0 })
            {
                inFile = filePayload.PaymentIds.Sum(id => byPayment.TryGetValue(id, out var p) ? p.NetAmount : 0m);
                unknownInFile = payload != null && filePayload.PaymentIds.Any(id => !byPayment.ContainsKey(id));
            }
            else
            {
                inFile = payload?.Payments.Where(p => string.Equals(p.PaymentMethod, "ACH", StringComparison.OrdinalIgnoreCase)).Sum(p => p.NetAmount) ?? 0m;
            }
            var voidedClaimed = voidEvents.Where(e => e.Status == GlSourceEventStatus.Posted).Sum(e => e.ClaimedAmount);

            var accrued = PayableCredit(accrualChain);
            var transmitted = -PayableCredit(achChain);
            var voided = -PayableCredit(voidChain);
            var reissued = -PayableCredit(reissueChain);
            var preCutover = accrualEvent is { Status: GlSourceEventStatus.Parked, ParkReason: GlParkReason.PreCutover };

            var flags = new List<GlReconciliationFlag>();
            if (preCutover)
            {
                flags.Add(GlReconciliationFlag.PreCutover);
            }
            else
            {
                if (accrualEvent == null) flags.Add(GlReconciliationFlag.AccrualMissing);
                else if (accrualEvent.Status == GlSourceEventStatus.Parked) flags.Add(GlReconciliationFlag.AccrualParked);
                else if (accrualLive != null && accrued != runNet) flags.Add(GlReconciliationFlag.AccrualAmountMismatch);
                if (fileEvents.Any(e => e.Status == GlSourceEventStatus.Parked && e.ParkReason != GlParkReason.DuplicateBusinessKey))
                    flags.Add(GlReconciliationFlag.TransmissionParked);
                if (voidEvents.Any(e => e.Status == GlSourceEventStatus.Parked))
                    flags.Add(GlReconciliationFlag.VoidParked);
                if (fileEvents.Count(e => e.Status is not (GlSourceEventStatus.Dismissed or GlSourceEventStatus.NothingToPost)) > 1)
                    flags.Add(GlReconciliationFlag.DuplicateTransmission);
                if (achLive != null && fileEvent?.EntryId == achLive.Id && fileEvent.ClaimedAmount != transmitted)
                    flags.Add(GlReconciliationFlag.TransmissionAmountMismatch);
                if (transmitted + voided + reissued > accrued) flags.Add(GlReconciliationFlag.TransmittedMoreThanAccrued);
                if (fileEvent != null && payload != null && (fileTotal != inFile || unknownInFile)) flags.Add(GlReconciliationFlag.FileDiffersFromAchPayments);
                if (accrued - transmitted - voided - reissued > 0m) flags.Add(GlReconciliationFlag.PayableOutstanding);
                if ((accrualChain.Count > 0 && accrualLive == null) || (achChain.Count > 0 && achLive == null))
                    flags.Add(GlReconciliationFlag.EntryReversed);
            }

            rows.Add(new RunRow(new GlRunReconciliation
            {
                PaymentRunId = runId,
                PaymentRunNumber = payload?.PaymentRunNumber ?? fileEvents.Select(e => e.SourceReference).FirstOrDefault() ?? string.Empty,
                RunNetTotal = runNet,
                RunAchTotal = inFile,
                RunVoidedTotal = voidedClaimed,
                RunCheckTotal = runNet - inFile - voidedClaimed,
                AccruedPayable = accrued,
                FileCreditTotal = fileTotal,
                TransmittedPayable = transmitted,
                VoidedPayable = voided,
                ReissuedPayable = reissued,
                OutstandingPayable = accrued - transmitted - voided - reissued,
                Flags = flags.Distinct().ToList(),
            }, accrualLive?.Period ?? accrualChain.FirstOrDefault()?.Period));
        }
        return rows;
    }
}
