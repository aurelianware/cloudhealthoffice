using CloudHealthOffice.PlanConfiguration;
using System.Globalization;
using PaymentService.Models;
using PaymentService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;

namespace PaymentService.Services;

/// <summary>
/// The NACHA CCD+ credit file of an executed fee-for-service payment run.
///
/// <list type="bullet">
/// <item>One credit per payee (payee TIN + approved account) per 835 trace
/// number, aggregating that payee's payments in the run; the amount is net of
/// any receivable offsets (PLB FB/WO), i.e. the 835's BPR02 share.</item>
/// <item>The account is read only from provider-service's active approved
/// account (dual control). A payee without one is paid by check and listed;
/// an unknown answer stops generation with nothing recorded.</item>
/// <item>The addenda carries <c>TRN*1*{TRN02}*{TRN03}\</c>, the TRN of the
/// payment's 835 (TRN02 = the payment's check/trace number, TRN03 =
/// Era:OriginatingCompanyId, also the batch company id).</item>
/// <item>Idempotent: the file depends only on the run and the approved accounts,
/// plus the header values chosen when it is first pinned and kept on the run (file
/// creation time = the run's execution, effective entry date = the first banking
/// day on or after the later of an explicitly requested payment date and the
/// earliest acceptable date, file ID modifier). The first generation pins the file's SHA-256 on the run;
/// a later generation must produce the same bytes, or it is refused
/// (an approved account changed since, or a payee lost its EFT account).</item>
/// </list>
///
/// Transmission to the bank is not done here: <see cref="IPaymentFileTransmissionService"/>
/// regenerates the file through this service, checks it against the pinned and
/// approved SHA-256, and sends it exactly once (off unless BankTransmission:Enabled).
/// </summary>
public interface IFfsEftFileService
{
    Task<FfsEftFileOutcome> GenerateAsync(string paymentRunId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the pinned file with a new one (new creation time, effective date
    /// chosen now, new file ID modifier, file reference and name ending -R{n}); the
    /// old one goes to <see cref="PaymentRun.EftFileHistory"/>. Only through
    /// <see cref="IPaymentFileTransmissionService.RedateAsync"/>, which checks the
    /// old file may be superseded.
    /// </summary>
    /// <remarks>
    /// Refused (<see cref="RunConflictException"/>) unless the pinned file is still
    /// <paramref name="expectedFileReference"/> with <paramref name="expectedSha256"/>.
    /// The run's 835s are not rewritten (their delivery is not tracked): each is listed
    /// in the new file's <see cref="PaymentRunEftFile.RemittanceDateNotices"/> and a run
    /// warning, carrying the old BPR16.
    /// </remarks>
    /// <param name="voids">
    /// Payments reversed before the file was sent: left out of the new file, listed in its
    /// <see cref="PaymentRunEftFile.VoidedPayments"/> (the provider must be told the 835's
    /// payment will not arrive), and announced to the GL (PaymentVoidedBeforeTransmission).
    /// </param>
    Task<FfsEftFileOutcome> RepinAsync(string paymentRunId, string actorUserId, string reason,
        string expectedFileReference, string expectedSha256, IReadOnlyList<PaymentVoid>? voids = null,
        CancellationToken cancellationToken = default);
}

/// <summary>A payment to leave out of a re-pinned file because it was reversed before the file was sent.</summary>
/// <param name="ReversalRunId">The reversal run(s) that recouped it (comma separated when split).</param>
/// <param name="Cancellations">What to cancel on each receivable its reversal opened (one per reversal 835).</param>
/// <param name="PayableRelieved">The payable that is never to be paid: what the reversal took back, net of the payment's own recoveries.</param>
/// <param name="UnpaidRemainder">The part of the payment that was not reversed: still owed to the provider, to be reissued.</param>
/// <param name="ReverseOffsets">The payment recovered receivables; those recoveries are undone (it is never made).</param>
/// <param name="ReversalExecutedAt">When the (latest) reversal run executed: decides the GL cutover of the void.</param>
/// <param name="ByOverride">Excluded by a finance decision (exclude-payments), not by the automatic re-pin.</param>
public sealed record PaymentVoid(string PaymentId, string? ReversalRunId, IReadOnlyList<ReceivableCancellation> Cancellations,
    decimal PayableRelieved, decimal UnpaidRemainder, bool ReverseOffsets, DateTime? ReversalExecutedAt, bool ByOverride);

public sealed record ReceivableCancellation(string ReceivableId, decimal Amount);

public sealed class FfsEftFileOutcome
{
    public required PaymentRun Run { get; init; }

    /// <summary>The file; null when no payee is paid by EFT. Holds full numbers: never returned to a caller.</summary>
    public FfsNachaBuiltFile? File { get; init; }

    /// <summary>True when the run already had a file and this generation reproduced it.</summary>
    public bool Reproduced { get; init; }
}

public sealed class FfsEftFileService : IFfsEftFileService
{
    private readonly IPaymentRunRepository _runs;
    private readonly IPaymentRepository _payments;
    private readonly IProviderPayeeAccountSource _accounts;
    private readonly IConfiguration _configuration;
    private readonly ICarrierDirectory? _carriers;
    private readonly INachaFileIdModifierAllocator _modifiers;
    private readonly AchEffectiveDatePolicy _dates;
    private readonly IEraEnvelopeRepository _envelopes;
    private readonly TimeProvider _time;
    private readonly ILogger<FfsEftFileService> _logger;

    public FfsEftFileService(
        IPaymentRunRepository runs,
        IPaymentRepository payments,
        IProviderPayeeAccountSource accounts,
        IConfiguration configuration,
        INachaFileIdModifierAllocator modifiers,
        AchEffectiveDatePolicy effectiveDates,
        IEraEnvelopeRepository envelopes,
        ILogger<FfsEftFileService> logger,
        TimeProvider? time = null,
        ICarrierDirectory? carriers = null)
    {
        _dates = effectiveDates;
        _envelopes = envelopes;
        _runs = runs;
        _payments = payments;
        _accounts = accounts;
        _modifiers = modifiers;
        _configuration = configuration;
        _carriers = carriers;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<FfsEftFileOutcome> GenerateAsync(string paymentRunId, string actorUserId, CancellationToken cancellationToken = default)
    {
        var (run, plan) = await LoadAndPlanAsync(paymentRunId, actorUserId, cancellationToken);
        var carrier = await CarrierAsync(run.TenantId, cancellationToken);

        // A pinned file is rebuilt from its own pinned header values (creation time,
        // effective date, modifier). A first file takes its creation time from the
        // run's execution, its effective date from the run's payment date (fixed at
        // execution, = the 835s' BPR16), and claims a free modifier for its creation
        // day so no two same-day files collide at the bank.
        DateTime created, effective;
        string modifier;
        if (run.EftFile != null)
        {
            created = run.EftFile.FileCreatedAt;
            effective = run.EftFile.EffectiveEntryDate;
            modifier = run.EftFile.FileIdModifier ?? (_configuration["Nacha:FileIdModifier"] ?? "A");
        }
        else
        {
            created = Minute(run.ExecutionCompletedAt ?? run.ExecutionStartedAt ?? run.CreatedAt);
            // The run's payment date, fixed at execution: the same date its 835s carry
            // in BPR16. If it can no longer be sent by the time the file is pinned, the
            // send is refused and the file is re-dated (audited, with a notice that the
            // 835s carry the old date), never silently given another date here.
            effective = run.PaymentDate.Date;
            modifier = plan.Entries.Count == 0 ? "A" : await AllocateAsync(run, carrier, created, effective, run.Id);
        }
        var header = BuildHeader(run, carrier, modifier, created, effective);
        var built = plan.Entries.Count == 0
            ? null
            : FfsNachaCreditFileBuilder.Build(header, plan.Entries.Select(e => e.Entry).ToList());

        var now = _time.GetUtcNow().UtcDateTime;
        var sha = built?.Sha256 ?? string.Empty;

        if (run.EftFile != null)
        {
            if (!string.Equals(run.EftFile.Sha256, sha, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "EFT file for payment run {RunNumber} no longer reproduces: pinned {Pinned}, now {Now}",
                    run.PaymentRunNumber, run.EftFile.Sha256, sha);
                throw new RunConflictException(
                    $"The EFT file of payment run {run.PaymentRunNumber} would differ from the one generated at {run.EftFile.FirstGeneratedAt:O} " +
                    "(a payee's approved bank account or EFT enrollment changed since). Nothing was regenerated; " +
                    "the pinned file stands and the change needs a person.");
            }

            run.EftFile.GenerationCount++;
            run.EftFile.LastVerifiedAt = now;
            run.EftFile.LastVerifiedBy = actorUserId;
            // Only the EFT-file fields, and only while the same file is pinned: a
            // concurrent write to the run (finalize retries, reservation outcomes) is kept.
            if (!await _runs.TrySaveEftFileAsync(run.Id, run.EftFile, run.EftFile.Sha256,
                    Array.Empty<CheckFallbackPayment>(), Array.Empty<string>()))
                throw new RunConflictException(
                    $"The EFT file of payment run {run.PaymentRunNumber} changed while it was being verified. Nothing was recorded; try again.");
            return new FfsEftFileOutcome { Run = run, File = built, Reproduced = true };
        }

        var file = NewFile(plan, built, header, modifier, now, actorUserId,
            $"FFS-{run.PaymentRunNumber}", $"ACH-FFS-{run.PaymentRunNumber}.ach", revision: 0);

        // Fallbacks found now (the account was gone since execution) join the run's list.
        var addFallbacks = new List<CheckFallbackPayment>();
        var addWarnings = new List<string>();
        foreach (var fallback in plan.CheckFallbacks.Where(f => f.DecidedAt == "EftFile"))
        {
            if (run.CheckFallbacks.All(f => f.PaymentId != fallback.PaymentId))
                addFallbacks.Add(fallback);
            addWarnings.Add(
                $"EFT file: payment {fallback.CheckNumber} to NPI {fallback.PayeeNpi} is not in the file and must be paid by check: {fallback.Reason}. " +
                "Its 835 already went out as ACH; tell the provider.");
        }
        foreach (var split in plan.Entries.GroupBy(e => e.Entry.ReassociationTrace, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            addWarnings.Add(
                $"EFT file: 835 trace {split.Key} is paid as {split.Count()} credits (its payees have different TINs or accounts); " +
                "each credit carries the trace, but no single credit equals that 835's BPR02");
        }

        // The first pin: only the EFT-file fields, and only while no file is pinned,
        // so two concurrent first generations can never pin two different files.
        if (!await _runs.TrySaveEftFileAsync(run.Id, file, null, addFallbacks, addWarnings))
        {
            var current = await _runs.GetByIdAsync(run.Id);
            if (current?.EftFile != null && string.Equals(current.EftFile.Sha256, sha, StringComparison.Ordinal))
                return new FfsEftFileOutcome { Run = current, File = built, Reproduced = true };
            throw new RunConflictException(
                $"Another request pinned a different EFT file for payment run {run.PaymentRunNumber} meanwhile. Nothing was recorded.");
        }
        run.EftFile = file;
        run.CheckFallbacks.AddRange(addFallbacks);
        run.Warnings.AddRange(addWarnings);

        _logger.LogInformation(
            "AUDIT EFT file {FileReference} for payment run {RunNumber} generated by {User}: {Entries} credits, {Total:F2}, {Checks} check fallbacks, " +
            "effective {Effective:yyyy-MM-dd}, sha256 {Sha}",
            file.FileReference, run.PaymentRunNumber, LogSanitizer.Sanitize(actorUserId),
            file.EntryCount, file.TotalCreditAmount, file.CheckFallbacks.Count, file.EffectiveEntryDate, file.Sha256);
        return new FfsEftFileOutcome { Run = run, File = built, Reproduced = false };
    }

    public async Task<FfsEftFileOutcome> RepinAsync(string paymentRunId, string actorUserId, string reason,
        string expectedFileReference, string expectedSha256, IReadOnlyList<PaymentVoid>? voids = null,
        CancellationToken cancellationToken = default)
    {
        voids ??= Array.Empty<PaymentVoid>();
        var excluded = voids.Select(v => v.PaymentId).ToHashSet(StringComparer.Ordinal);
        var (run, plan) = await LoadAndPlanAsync(paymentRunId, actorUserId, cancellationToken, excluded);
        var old = run.EftFile
            ?? throw new InvalidOperationException($"Payment run {run.PaymentRunNumber} has no EFT file to re-date.");
        // Only the file the caller checked may be superseded: a concurrent re-date that
        // already replaced it (and whose file may already be approved and sent) wins.
        if (!string.Equals(old.FileReference, expectedFileReference, StringComparison.Ordinal)
            || !string.Equals(old.Sha256, expectedSha256, StringComparison.Ordinal))
            throw new RunConflictException(
                $"Payment run {run.PaymentRunNumber}'s pinned file is now {old.FileReference}, not {expectedFileReference}: " +
                "it was re-dated meanwhile. Nothing was changed.");
        if (plan.Entries.Count == 0 && voids.Count == 0)
            throw new InvalidOperationException($"Payment run {run.PaymentRunNumber} has no EFT credits any more; there is nothing to re-date.");

        var carrier = await CarrierAsync(run.TenantId, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        var revision = old.Revision + 1;
        // A new file: created now, with its own modifier for today. It keeps the old
        // file's date while that can still be sent (a re-pin only to leave out voided
        // payments); otherwise the date is chosen afresh.
        var created = Minute(now);
        var effective = _dates.SendProblem(old.EffectiveEntryDate) == null
            ? old.EffectiveEntryDate.Date
            : _dates.Choose(run.PaymentDateRequested ? run.PaymentDate : null);
        var modifier = await AllocateAsync(run, carrier, created, effective, $"{run.Id}#r{revision}");
        var header = BuildHeader(run, carrier, modifier, created, effective);
        // Every payment voided: an empty pin (nothing to send), kept for the record.
        var built = plan.Entries.Count == 0 ? null : FfsNachaCreditFileBuilder.Build(header, plan.Entries.Select(e => e.Entry).ToList());
        var file = NewFile(plan, built, header, modifier, now, actorUserId,
            $"FFS-{run.PaymentRunNumber}-R{revision}", $"ACH-FFS-{run.PaymentRunNumber}-R{revision}.ach", revision);
        file.EffectiveEntryDate = DateTime.SpecifyKind(effective.Date, DateTimeKind.Utc);

        // The run's 835s were built at execution with BPR16 = the run's payment date.
        // payment-service does not track whether a trading partner has fetched an 835
        // (GET /api/era-envelopes/{id}/edi), so they are treated as delivered and are
        // never rewritten: each one is listed with its BPR16 and the new effective date,
        // so the providers can be told the funds settle on another day.
        var envelopes = ((await _envelopes.GetByPaymentRunIdAsync(run.Id)) ?? Enumerable.Empty<EraEnvelopeRecord>()).ToList();
        foreach (var envelope in envelopes.Where(_ => file.EffectiveEntryDate.Date != run.PaymentDate.Date))
        {
            file.RemittanceDateNotices.Add(new RemittanceDateNotice
            {
                EraEnvelopeId = envelope.Id,
                TradingPartnerId = envelope.TradingPartnerId,
                ControlNumber = envelope.ControlNumber,
                Bpr16Date = run.PaymentDate.Date,
                EffectiveEntryDate = file.EffectiveEntryDate,
            });
        }
        var notice = file.RemittanceDateNotices.Count == 0 ? null
            : $"EFT file re-dated to {file.EffectiveEntryDate:yyyy-MM-dd}: {file.RemittanceDateNotices.Count} 835(s) already issued for this run " +
              $"carry BPR16 {run.PaymentDate:yyyy-MM-dd} ({string.Join(", ", file.RemittanceDateNotices.Select(n => $"{n.TradingPartnerId} ISA13 {n.ControlNumber}"))}); " +
              "tell the providers the funds settle on the new date.";

        // Voided payments: listed on the new file (their 835 said they would be paid; the
        // providers must be told they will not) and announced to the GL, in the same write.
        var warnings = new List<string>();
        if (notice != null) warnings.Add(notice);
        var glEvents = new List<PaymentFileOutboxMessage>();
        // Earlier re-pins' voids stay listed: the notice and its completion are part of the file's record.
        file.VoidedPayments.AddRange(old.VoidedPayments);
        foreach (var v in voids.Where(v => old.VoidedPayments.All(x => x.PaymentId != v.PaymentId)))
        {
            var payment = await _payments.GetByIdAsync(v.PaymentId)
                          ?? throw new InvalidOperationException($"Payment {v.PaymentId} does not exist; nothing was re-pinned.");
            var envelope = envelopes.FirstOrDefault(e => e.Id == payment.EraEnvelopeId);
            file.VoidedPayments.Add(new VoidedPaymentNotice
            {
                PaymentId = payment.Id,
                VoidedBy = actorUserId,
                CheckNumber = payment.CheckNumber,
                PayeeNpi = payment.PayeeNPI ?? string.Empty,
                Amount = payment.TotalPaymentAmount,
                EraEnvelopeId = payment.EraEnvelopeId,
                TradingPartnerId = envelope?.TradingPartnerId,
                ControlNumber = envelope?.ControlNumber,
                ReversalRunId = v.ReversalRunId,
                ReceivableId = v.Cancellations.FirstOrDefault()?.ReceivableId,
                Cancellations = v.Cancellations.Select(c => new VoidedReceivableCancellation { ReceivableId = c.ReceivableId, Amount = c.Amount }).ToList(),
                PayableRelieved = v.PayableRelieved,
                UnpaidRemainder = v.UnpaidRemainder,
                OffsetsReversed = v.ReverseOffsets,
                ExcludedByOverride = v.ByOverride,
                ReversalExecutedAt = v.ReversalExecutedAt,
            });
            glEvents.Add(GlEventOutbox.PaymentVoidedBeforeTransmission(run, payment, v, old.FileReference, file.FileReference, actorUserId, now));
            warnings.Add($"Payment {payment.CheckNumber} ({payment.TotalPaymentAmount:F2}) to NPI {payment.PayeeNPI} was reversed before its EFT file was sent " +
                         $"and is left out of {file.FileReference}: it will not be paid. Its 835 ({envelope?.TradingPartnerId} ISA13 {envelope?.ControlNumber}) " +
                         "said it would be; tell the provider." +
                         (v.UnpaidRemainder > 0m
                             ? $" {v.UnpaidRemainder:F2} of it was not reversed: that is still owed to the provider and must be reissued by a person."
                             : string.Empty));
        }

        old.SupersededAt = now;
        old.SupersededBy = actorUserId;
        old.SupersededReason = reason;
        old.SupersededByFileReference = file.FileReference;
        if (!await _runs.TryRepinEftFileAsync(run.Id, file, old, warnings, glEvents))
            throw new RunConflictException(
                $"The EFT file of payment run {run.PaymentRunNumber} changed while it was being re-dated. Nothing was recorded.");
        run.EftFileHistory.Add(old);
        run.EftFile = file;
        // Written in the same conditional re-pin above: they cannot be lost to a race.
        run.Warnings.AddRange(warnings);
        run.GlOutbox.AddRange(glEvents);
        foreach (var w in warnings)
            _logger.LogWarning("AUDIT {Notice} (payment run {RunNumber})", LogSanitizer.Sanitize(w), LogSanitizer.Sanitize(run.PaymentRunNumber));

        _logger.LogWarning(
            "AUDIT EFT file {Old} (effective {OldDate:yyyy-MM-dd}, sha256 {OldSha}) of payment run {RunNumber} re-dated by {User} as {New} " +
            "(effective {NewDate:yyyy-MM-dd}, modifier {Modifier}, sha256 {NewSha}): {Reason}",
            old.FileReference, old.EffectiveEntryDate, old.Sha256, run.PaymentRunNumber, LogSanitizer.Sanitize(actorUserId), file.FileReference,
            file.EffectiveEntryDate, modifier, file.Sha256, LogSanitizer.Sanitize(reason));
        return new FfsEftFileOutcome { Run = run, File = built, Reproduced = false };
    }

    private async Task<(PaymentRun Run, Plan Plan)> LoadAndPlanAsync(string paymentRunId, string actorUserId, CancellationToken cancellationToken,
        IReadOnlySet<string>? excluded = null)
    {
        var run = await _runs.GetByIdAsync(paymentRunId)
                  ?? throw new KeyNotFoundException($"Payment run {paymentRunId} not found");
        if (run.Status != PaymentRunStatus.Completed)
            throw new InvalidOperationException($"Payment run {run.PaymentRunNumber} is {run.Status}; only a completed run has an EFT file");
        if (!string.Equals(run.PaymentMethod, "ACH", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Payment run {run.PaymentRunNumber} pays by {run.PaymentMethod}; only an ACH run has an EFT file");
        if (!_accounts.IsConfigured)
            throw new InvalidOperationException(UnconfiguredProviderPayeeAccountSource.Reason);

        // provider-service is called with payment-service's own token, for the
        // run's tenant, under the user who asked for the file.
        using var grant = RunExecutionGrant.Open(run.TenantId, run.Id, actorUserId);

        // Payments a re-pin left out (voided before transmission) stay out of every rebuild,
        // also before the void is completed on the payment itself.
        var voided = run.EftFile?.VoidedPayments.Select(v => v.PaymentId).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        var payments = new List<Payment>();
        foreach (var id in run.PaymentIds)
        {
            var payment = await _payments.GetByIdAsync(id);
            // Voided before transmission (now or by an earlier re-pin): never in a file again.
            if (payment != null && !payment.IsReversal && payment.VoidedBeforePaymentAt == null && excluded?.Contains(payment.Id) != true
                && !voided.Contains(payment.Id))
                payments.Add(payment);
        }

        return (run, await PlanAsync(run, payments, cancellationToken));
    }

    private Task<string> AllocateAsync(
        PaymentRun run, CarrierIdentity? carrier, DateTime created, DateTime effective, string holder)
    {
        // The modifier is claimed per destination and origin, so the probe must
        // carry the same origination the file will: a carrier that states its
        // own origin is a different originator at the bank.
        var probe = BuildHeader(run, carrier, "A", created, effective);
        return _modifiers.AllocateAsync(run.TenantId, probe.ImmediateDestination, probe.ImmediateOrigin, created.Date, holder);
    }

    private static DateTime Minute(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc);

    private static PaymentRunEftFile NewFile(Plan plan, FfsNachaBuiltFile? built, FfsNachaFileHeader header, string modifier,
        DateTime now, string actorUserId, string reference, string name, int revision)
    {
        var file = new PaymentRunEftFile
        {
            FileReference = reference,
            FileName = name,
            Revision = revision,
            Sha256 = built?.Sha256 ?? string.Empty,
            ByteSize = built?.ByteSize ?? 0,
            EntryCount = built?.EntryCount ?? 0,
            AddendaCount = built?.AddendaCount ?? 0,
            BatchCount = built?.BatchCount ?? 0,
            BlockCount = built?.BlockCount ?? 0,
            EntryHash = built?.EntryHash ?? string.Empty,
            TotalCreditAmount = built?.TotalCreditAmount ?? 0m,
            TotalDebitAmount = 0m,
            FileCreatedAt = header.FileCreatedAt,
            EffectiveEntryDate = header.EffectiveEntryDate,
            FileIdModifier = built == null ? null : modifier,
            FirstGeneratedAt = now,
            FirstGeneratedBy = actorUserId,
            LastVerifiedAt = now,
            LastVerifiedBy = actorUserId,
            GenerationCount = 1,
            CheckFallbacks = plan.CheckFallbacks,
            ZeroAmountPaymentIds = plan.ZeroAmountPaymentIds,
        };
        for (var i = 0; i < plan.Entries.Count; i++)
        {
            var p = plan.Entries[i];
            file.Entries.Add(new PaymentRunEftEntry
            {
                AchTraceNumber = built!.AchTraceNumbers[i],
                ReassociationTrace = p.Entry.ReassociationTrace,
                Amount = p.Entry.Amount,
                PayeeNpis = p.PayeeNpis,
                ReceiverName = p.Entry.ReceiverName,
                TaxIdLast4 = Last4(p.TaxIdDigits),
                RoutingNumberLast4 = Last4(p.Entry.RoutingNumber),
                AccountNumberLast4 = Last4(p.Entry.AccountNumber),
                TransactionCode = p.Entry.IsSavings ? "32" : "22",
                PaymentIds = p.PaymentIds,
                ClaimCount = p.ClaimCount,
            });
        }
        return file;
    }

    private sealed class PlannedEntry
    {
        public required FfsNachaCreditEntry Entry { get; init; }
        public required string TaxIdDigits { get; init; }
        public List<string> PayeeNpis { get; init; } = new();
        public List<string> PaymentIds { get; init; } = new();
        public int ClaimCount { get; init; }
    }

    private sealed class Plan
    {
        public List<PlannedEntry> Entries { get; } = new();
        public List<CheckFallbackPayment> CheckFallbacks { get; } = new();
        public List<string> ZeroAmountPaymentIds { get; } = new();
    }

    private async Task<Plan> PlanAsync(PaymentRun run, List<Payment> payments, CancellationToken cancellationToken)
    {
        var plan = new Plan();
        var lookups = new Dictionary<string, PayeeAccountLookup>(StringComparer.Ordinal);
        var eft = new List<(Payment Payment, PayeeEftAccount Account)>();

        foreach (var payment in payments.OrderBy(p => p.CheckNumber, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            if (payment.TotalPaymentAmount <= 0m)
            {
                plan.ZeroAmountPaymentIds.Add(payment.Id);
                continue;
            }

            var recorded = run.CheckFallbacks.FirstOrDefault(f => f.PaymentId == payment.Id);
            if (!string.Equals(payment.PaymentMethod, "ACH", StringComparison.OrdinalIgnoreCase))
            {
                plan.CheckFallbacks.Add(recorded ?? new CheckFallbackPayment
                {
                    PaymentId = payment.Id,
                    PayeeNpi = payment.PayeeNPI,
                    PayeeName = payment.PayeeName,
                    CheckNumber = payment.CheckNumber,
                    Amount = payment.TotalPaymentAmount,
                    Reason = $"the payment was issued as {payment.PaymentMethod}",
                    DecidedAt = "Execution",
                });
                continue;
            }

            var npi = payment.PayeeNPI ?? string.Empty;
            if (!lookups.TryGetValue(npi, out var lookup))
            {
                lookup = await _accounts.GetAsync(run.TenantId, npi, cancellationToken);
                lookups[npi] = lookup;
            }

            // An approved account whose routing number fails the ABA check digit
            // cannot be credited (the bank would reject the whole file): check.
            if (lookup.Status == PayeeAccountLookupStatus.Eft
                && !FfsNachaCreditFileBuilder.IsValidAbaRoutingNumber(lookup.Account!.RoutingNumber))
                lookup = PayeeAccountLookup.NoEft("the approved bank account's routing number fails the ABA check digit");

            switch (lookup.Status)
            {
                case PayeeAccountLookupStatus.Eft:
                    eft.Add((payment, lookup.Account!));
                    break;
                case PayeeAccountLookupStatus.NoEftAccount:
                    plan.CheckFallbacks.Add(new CheckFallbackPayment
                    {
                        PaymentId = payment.Id,
                        PayeeNpi = payment.PayeeNPI,
                        PayeeName = payment.PayeeName,
                        CheckNumber = payment.CheckNumber,
                        Amount = payment.TotalPaymentAmount,
                        Reason = lookup.Reason ?? "no approved EFT account",
                        DecidedAt = "EftFile",
                        NeedsAttention = true,
                    });
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Cannot tell whether payee NPI {npi} has an approved EFT account ({lookup.Reason}); no EFT file was generated");
            }
        }

        // One credit per payee (TIN + account) per 835 trace number.
        var groups = eft
            .GroupBy(x => (Tin: x.Account.TaxIdDigits, x.Account.RoutingNumber, x.Account.AccountNumber, x.Account.IsSavings, Trace: x.Payment.CheckNumber))
            .OrderBy(g => g.Key.Trace, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Tin, StringComparer.Ordinal)
            .ThenBy(g => g.Key.RoutingNumber, StringComparer.Ordinal)
            .ThenBy(g => g.Key.AccountNumber, StringComparer.Ordinal)
            .ThenBy(g => g.Key.IsSavings);

        foreach (var group in groups)
        {
            var members = group.ToList();
            var npis = members.Select(m => m.Payment.PayeeNPI ?? string.Empty).Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
            var name = members.Select(m => m.Account.AccountHolderName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                       ?? members.Select(m => m.Payment.PayeeName).OrderBy(n => n, StringComparer.Ordinal).First();
            plan.Entries.Add(new PlannedEntry
            {
                Entry = new FfsNachaCreditEntry
                {
                    RoutingNumber = group.Key.RoutingNumber,
                    AccountNumber = group.Key.AccountNumber,
                    IsSavings = group.Key.IsSavings,
                    Amount = members.Sum(m => m.Payment.TotalPaymentAmount),
                    IdentificationNumber = npis.FirstOrDefault() ?? string.Empty,
                    ReceiverName = name,
                    ReassociationTrace = group.Key.Trace,
                },
                TaxIdDigits = group.Key.Tin,
                PayeeNpis = npis,
                PaymentIds = members.Select(m => m.Payment.Id).ToList(),
                ClaimCount = members.Sum(m => m.Payment.ClaimPayments.Count),
            });
        }

        return plan;
    }

    /// <summary>
    /// The tenant's carrier where it states an origination, and configuration
    /// (<c>Nacha:*</c>, <c>Payer:Name</c>) for everything it leaves unstated
    /// (plan-configuration.md §12.3).
    ///
    /// <para>
    /// The batch company id is the 835 TRN03 (<c>Era:OriginatingCompanyId</c>),
    /// and a stated company id that differs from it is refused, whether it came
    /// from the carrier or from configuration: the provider reassociates the
    /// deposit with the remittance on that one value, so the two cannot be
    /// allowed to drift. A carrier that states nothing leaves the deployment's
    /// values in place.
    /// </para>
    /// </summary>
    private FfsNachaFileHeader BuildHeader(
        PaymentRun run, CarrierIdentity? carrier, string fileIdModifier, DateTime created, DateTime effective)
    {
        var companyId = _configuration["Era:OriginatingCompanyId"] ?? string.Empty;
        var statedCompanyId = Stated(carrier?.NachaCompanyIdentification) ?? _configuration["Nacha:CompanyId"];
        if (!string.IsNullOrEmpty(statedCompanyId) && !string.Equals(statedCompanyId, companyId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The originating company id must equal Era:OriginatingCompanyId (the 835 TRN03); otherwise providers cannot "
                + $"reassociate the EFT with the 835. Carrier '{carrier?.Code ?? "(none)"}' or Nacha:CompanyId states "
                + "a different one.");

        var destination = _configuration["Nacha:ImmediateDestination"] ?? string.Empty;
        // The carrier's letterhead first, as in premium-billing and capitation:
        // a stated carrier name is the more specific answer, and a deployment
        // that has stated none keeps its own configured names.
        var letterhead = carrier?.LetterheadName;

        return new FfsNachaFileHeader
        {
            ImmediateDestination = destination,
            ImmediateOrigin = Stated(carrier?.NachaImmediateOrigin) ?? _configuration["Nacha:ImmediateOrigin"] ?? companyId,
            ImmediateDestinationName = _configuration["Nacha:ImmediateDestinationName"] ?? string.Empty,
            ImmediateOriginName = letterhead ?? _configuration["Nacha:ImmediateOriginName"] ?? _configuration["Payer:Name"] ?? string.Empty,
            CompanyName = letterhead ?? _configuration["Nacha:CompanyName"] ?? _configuration["Payer:Name"] ?? string.Empty,
            CompanyId = companyId,
            OriginatingDfi = _configuration["Nacha:OriginatingDfi"] ?? (destination.Length >= 8 ? destination[..8] : string.Empty),
            CompanyDiscretionaryData = _configuration["Nacha:CompanyDiscretionaryData"],
            ReferenceCode = Last(run.PaymentRunNumber, 8),
            FileIdModifier = fileIdModifier,
            FileCreatedAt = created,
            EffectiveEntryDate = effective.Date,
        };
    }

    /// <summary>
    /// The tenant's carrier, or null where this host runs without a carrier
    /// directory. An unreachable tenant-service throws rather than answering
    /// null: a carrier that states nothing and a carrier that could not be read
    /// look the same from here, and only the first is safe to pay under the
    /// configured entity.
    /// </summary>
    private Task<CarrierIdentity?> CarrierAsync(string tenantId, CancellationToken ct) =>
        _carriers is null || string.IsNullOrWhiteSpace(tenantId)
            ? Task.FromResult<CarrierIdentity?>(null)
            : _carriers.DefaultAsync(tenantId, ct);

    private static string? Stated(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Last4(string? value)
        => string.IsNullOrEmpty(value) ? null : value.Length <= 4 ? value : value[^4..];

    private static string Last(string value, int length)
        => value.Length <= length ? value : value[^length..];
}
