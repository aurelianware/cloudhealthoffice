using System.Text.Json;
using CloudHealthOffice.Finance.Contracts;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using PaymentService.Repositories;
using PaymentService.Services;
using CloudHealthOffice.PaymentService.Tests.Security;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// A payment reversed before its run's NACHA file was sent: the re-pin leaves it out (fresh
/// approval, audit), the payment is voided before payment, the receivable its reversal opened is
/// cancelled, the provider is listed for a "this 835 payment will not arrive" notice, and the GL
/// gets PaymentVoidedBeforeTransmission. The other payees are paid. Nothing connects anywhere.
/// </summary>
public class PaymentVoidBeforeTransmissionTests
{
    private const string Npi = "1111111111";
    private const string OtherNpi = "2222222222";

    private readonly FfsRunHarness _h = new();
    private readonly InMemoryPaymentFileTransmissionRepository _store = new();
    private readonly InMemoryBank _bank = new();
    private readonly CapturingLogger<PaymentFileTransmissionService> _log = new();
    private readonly MutableClock _clock = new();
    private readonly BankTransmissionOptions _options = new() { Enabled = true };

    public PaymentVoidBeforeTransmissionTests()
    {
        _h.Partner(Npi, "TP-A");
        _h.Partner(OtherNpi, "TP-A");
        _h.Accounts.Eft(Npi);
        _h.Accounts.Eft(OtherNpi, account: "999988887777");
        _h.Clock = _clock;
        _h.DateOptions = _options;
    }

    private PaymentFileTransmissionService Service(string user = "treasury-1")
        => new(_h.Runs, _h.Payments, _h.Reservations, _h.EftFiles(), _store, _bank, _bank,
            new TestActor(user, FfsRunHarness.Tenant).SeparationOfDuties(),
            Options.Create(_options), new AchEffectiveDatePolicy(Options.Create(_options), _clock), _log, _clock, _h.Ledger);

    private async Task<(PaymentRun Run, Payment Voided, Payment Kept)> PinnedRunAsync(decimal expectedTotal = 250m)
    {
        var run = await _h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 125m), FfsRunHarness.Claim("c2", Npi, 75m), FfsRunHarness.Claim("c3", OtherNpi, 50m));
        var pinned = (await _h.EftFiles().GenerateAsync(run.Id, "approver-2")).Run;
        Assert.Equal(expectedTotal, pinned.EftFile!.TotalCreditAmount);
        var payments = _h.Payments.All.Where(p => p.RunId == run.Id && !p.IsReversal).ToList();
        return (pinned, payments.Single(p => p.PayeeNPI == OtherNpi), payments.First(p => p.PayeeNPI == Npi));
    }

    /// <summary>What a completed reversal run leaves behind for <paramref name="claimIds"/>: reservations, reversal payments in one 835, the receivable.</summary>
    private async Task<ProviderReceivableRecord?> ReverseAsync(Payment original, string reversalRunId = "rr-1", string envelopeId = "env-rev-1",
        bool complete = true, bool receivable = true, bool held = false, params string[] claimIds)
    {
        var claims = claimIds.Length > 0 ? claimIds : original.ClaimPayments.Select(c => c.ClaimId).ToArray();
        foreach (var claimId in claims)
        {
            Assert.True(await _h.Reservations.TryReserveAsync(new ClaimReservation
            {
                TenantId = FfsRunHarness.Tenant, Kind = ClaimReservationKind.Reversal, ClaimId = claimId, RunId = reversalRunId, RunNumber = "RR-1",
            }));
            if (!complete)
                continue;
            var paid = original.ClaimPayments.Single(c => c.ClaimId == claimId).PaymentAmount;
            await _h.Payments.CreateAsync(new Payment
            {
                CheckNumber = "R-" + claimId, PaymentMethod = "REV", TotalPaymentAmount = -paid, PayeeNPI = original.PayeeNPI,
                RunId = reversalRunId, IsReversal = true, EraEnvelopeId = envelopeId,
                ClaimPayments = { new ClaimPayment { ClaimId = claimId, PaymentAmount = -paid } },
            });
        }
        if (!receivable)
            return null;
        var owed = claims.Sum(c => original.ClaimPayments.Single(x => x.ClaimId == c).PaymentAmount);
        if (!held)
            return await _h.SeedReceivableAsync(original.PayeeNPI!, owed, envelopeId: envelopeId);
        // As the reversal run now records it: the original's file was not sent, so it is held.
        return await _h.Ledger.RecordForwardBalanceAsync(FfsRunHarness.Tenant, original.PayeeNPI!, "TP-A",
            new ReversalRun { Id = reversalRunId, ReversalRunNumber = "RR-1", TenantId = FfsRunHarness.Tenant },
            envelopeId, "R-TRACE", owed, "approver-0", new[] { original.Id });
    }

    [Fact]
    public async Task A_payment_reversed_before_transmission_is_left_out_and_everyone_else_is_paid()
    {
        var (run, voided, kept) = await PinnedRunAsync();
        var receivable = (await ReverseAsync(voided))!;
        Assert.Equal(50m, receivable.OutstandingAmount);

        // The approval no longer describes the money: refused, pointing at the re-pin.
        var stale = await Assert.ThrowsAsync<PaymentFileApprovalStaleException>(() => Service().TransmitAsync(run.Id));
        Assert.Contains("re-pin", stale.Message);
        Assert.Empty(_bank.Sent);

        // The re-pin (the re-date path; its date is still sendable, so it is kept).
        var repinned = await Service("treasury-2").RedateAsync(run.Id, "payment reversed before transmission");

        Assert.Equal("FFS-" + run.PaymentRunNumber + "-R1", repinned.FileReference);
        Assert.Equal(run.EftFile!.EffectiveEntryDate.Date, repinned.EffectiveEntryDate.Date);
        Assert.Empty(repinned.RemittanceDateNotices); // the date did not move
        Assert.Equal(200m, repinned.TotalCreditAmount);
        Assert.DoesNotContain(repinned.Entries, e => e.PaymentIds.Contains(voided.Id));
        var notice = Assert.Single(repinned.VoidedPayments);
        Assert.Equal((voided.Id, voided.CheckNumber, OtherNpi, 50m, "TP-A", "rr-1", receivable.Id, true),
            (notice.PaymentId, notice.CheckNumber, notice.PayeeNpi, notice.Amount, notice.TradingPartnerId, notice.ReversalRunId, notice.ReceivableId, notice.Completed));
        Assert.False(string.IsNullOrEmpty(notice.ControlNumber)); // the 835 the provider must be told about

        // The old file can never be sent; the new one needs a fresh approval.
        Assert.Equal(PaymentFileTransmissionStatus.Superseded, (await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile.FileReference))!.Status);
        Assert.Null(await _store.GetAsync(FfsRunHarness.Tenant, repinned.FileReference));

        // Voided before payment; the receivable is cancelled (never paid, so nothing is owed).
        var payment = (await _h.Payments.GetByIdAsync(voided.Id))!;
        Assert.NotNull(payment.VoidedBeforePaymentAt);
        Assert.Equal(("treasury-2", repinned.FileReference), (payment.VoidedBeforePaymentBy, payment.VoidedBeforePaymentFileReference));
        var cancelled = (await _h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!;
        Assert.Equal((0m, ReceivableStatus.Cancelled), (cancelled.OutstandingAmount, cancelled.Status));
        Assert.Contains(cancelled.Entries, e => e.Type == ReceivableEntryType.CancelledUnpaid && e.Amount == 50m);
        Assert.Empty(await _h.LedgerStore.ListOutstandingAsync(FfsRunHarness.Tenant, OtherNpi));

        // The GL event and the 835 notice, in the same write as the re-pin.
        var stored = (await _h.Runs.GetByIdAsync(run.Id))!;
        var message = Assert.Single(stored.GlOutbox, m => m.Type == GlEventTypes.PaymentVoidedBeforeTransmission);
        var payload = JsonSerializer.Deserialize<PaymentVoidedBeforeTransmissionEvent>(message.PayloadJson, GlEventTypes.Json)!;
        Assert.Equal((voided.Id, 50m, "rr-1", run.EftFile.FileReference, repinned.FileReference),
            (payload.PaymentId, payload.Amount, payload.ReversalRunId, payload.SupersededFileReference, payload.NewFileReference));
        Assert.Equal(run.ExecutionStartedAt, payload.PaymentRunExecutedAt);
        Assert.Contains(stored.Warnings, w => w.Contains(voided.CheckNumber) && w.Contains("tell the provider"));
        Assert.Contains("voided before transmission", _log.All);

        // The re-dater cannot approve it; someone else sends it, once, for the other payees only.
        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("treasury-2").TransmitAsync(run.Id));
        var sent = await Service("treasury-3").TransmitAsync(run.Id);
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, sent.Status);
        Assert.Equal(200m, sent.TotalCreditAmount);
        Assert.Equal(voided.Id, Assert.Single(sent.VoidedPayments).PaymentId);
        Assert.DoesNotContain(voided.Id, sent.ApprovedPaymentIds);
        Assert.Contains(kept.Id, sent.ApprovedPaymentIds);
        var transmitted = JsonSerializer.Deserialize<PaymentFileTransmittedEvent>(Assert.Single(sent.Outbox).PayloadJson, GlEventTypes.Json)!;
        Assert.Equal(sent.ApprovedPaymentIds, transmitted.PaymentIds);
        Assert.Equal(run.ExecutionStartedAt, transmitted.PaymentRunExecutedAt);
        Assert.Single(_bank.Sent);
    }

    [Fact]
    public async Task An_interrupted_void_is_finished_before_the_next_send()
    {
        var (run, voided, _) = await PinnedRunAsync();
        var receivable = (await ReverseAsync(voided))!;
        // The re-pin lands but the cancellation of the receivable fails once (the ledger is unreachable):
        // the re-pin still succeeds; the void is finished later.
        var flaky = new FlakyReceivables(_h.LedgerStore) { FailNextReplace = true };
        var redater = WithLedger("treasury-2", flaky);

        await redater.RedateAsync(run.Id, "payment reversed");

        var pinned = (await _h.Runs.GetByIdAsync(run.Id))!.EftFile!;
        Assert.False(Assert.Single(pinned.VoidedPayments).Completed);
        Assert.Null((await _h.Payments.GetByIdAsync(voided.Id))!.VoidedBeforePaymentAt);
        // Even unfinished, the voided payment is never rebuilt into the file.
        Assert.Equal(pinned.Sha256, (await _h.EftFiles().GenerateAsync(run.Id, "approver-2")).Run.EftFile!.Sha256);

        var sent = await Service("treasury-3").TransmitAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, sent.Status);
        Assert.True(Assert.Single((await _h.Runs.GetByIdAsync(run.Id))!.EftFile!.VoidedPayments).Completed);
        Assert.Equal(ReceivableStatus.Cancelled, (await _h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!.Status);
        Assert.NotNull((await _h.Payments.GetByIdAsync(voided.Id))!.VoidedBeforePaymentAt);
    }

    [Fact]
    public async Task A_partly_reversed_payment_needs_a_person_and_nothing_changes()
    {
        var (run, _, kept) = await PinnedRunAsync();
        Assert.Equal(2, kept.ClaimPayments.Count);
        await ReverseAsync(kept, claimIds: kept.ClaimPayments[0].ClaimId);

        var ex = await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-2").RedateAsync(run.Id, "reversed"));

        Assert.Contains("only 1 of its 2 claims", ex.Message);
        Assert.Null(await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile!.FileReference));
        Assert.Equal(run.EftFile.Sha256, (await _h.Runs.GetByIdAsync(run.Id))!.EftFile!.Sha256);
    }

    [Fact]
    public async Task A_reversal_still_in_progress_or_without_its_receivable_is_refused()
    {
        var (run, voided, _) = await PinnedRunAsync();
        await ReverseAsync(voided, complete: false, receivable: false);

        var running = await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-2").RedateAsync(run.Id, "reversed"));

        Assert.Contains("has not completed", running.Message);
        Assert.Equal(run.EftFile!.Sha256, (await _h.Runs.GetByIdAsync(run.Id))!.EftFile!.Sha256);
    }

    [Fact]
    public async Task A_reversal_that_opened_no_receivable_is_refused()
    {
        var (run, voided, _) = await PinnedRunAsync();
        await ReverseAsync(voided, receivable: false);

        var ex = await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-2").RedateAsync(run.Id, "reversed"));

        Assert.Contains("opened no receivable", ex.Message);
        Assert.Null(await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile!.FileReference));
    }

    private PaymentFileTransmissionService WithLedger(string user, IProviderReceivableRepository store)
        => new(_h.Runs, _h.Payments, _h.Reservations, _h.EftFiles(), _store, _bank, _bank,
            new TestActor(user, FfsRunHarness.Tenant).SeparationOfDuties(), Options.Create(_options),
            new AchEffectiveDatePolicy(Options.Create(_options), _clock), _log, _clock,
            new ProviderReceivableLedger(store, _h.Configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderReceivableLedger>.Instance));

    private Payment LaterPayment(string npi, decimal amount) => new()
    {
        Id = "later-" + Guid.NewGuid().ToString("N")[..6], CheckNumber = "0009990001", PayeeNPI = npi, PaymentMethod = "ACH",
        TotalPaymentAmount = amount, RunId = "run-later", PaymentDate = new DateTime(2026, 5, 1),
    };

    [Fact]
    public async Task A_receivable_held_for_an_unsent_payment_is_never_recovered_before_the_void()
    {
        var (run, voided, _) = await PinnedRunAsync();
        var receivable = (await ReverseAsync(voided, held: true))!;
        Assert.Equal(ReceivableStatus.PendingVoid, receivable.Status);
        Assert.Equal(new[] { voided.Id }, receivable.PendingPaymentIds);

        // A later payment run to the same provider takes nothing from it.
        var later = LaterPayment(OtherNpi, 30m);
        Assert.Empty(await _h.Ledger.ApplyRecoveriesAsync(FfsRunHarness.Tenant, later, "run-later", "PR-LATER", "approver-9"));
        Assert.Equal(30m, later.TotalPaymentAmount);
        Assert.Empty(await _h.LedgerStore.ListOutstandingAsync(FfsRunHarness.Tenant, OtherNpi));

        await Service("treasury-2").RedateAsync(run.Id, "payment reversed before transmission");

        var cancelled = (await _h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!;
        Assert.Equal((ReceivableStatus.Cancelled, 0m, 0m), (cancelled.Status, cancelled.OutstandingAmount, cancelled.RecoveredAmount));
        Assert.Empty(cancelled.PendingPaymentIds);
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service("treasury-3").TransmitAsync(run.Id)).Status);
    }

    [Fact]
    public async Task A_held_receivable_is_released_when_the_payment_turns_out_to_have_been_sent()
    {
        var (run, _, kept) = await PinnedRunAsync();
        // Held for "kept" (its reversal ran while the file was, say, awaiting review); then the file is confirmed sent.
        var held = await _h.Ledger.RecordForwardBalanceAsync(FfsRunHarness.Tenant, Npi, "TP-A",
            new ReversalRun { Id = "rr-9", ReversalRunNumber = "RR-9", TenantId = FfsRunHarness.Tenant }, "env-rev-9", "R-9", 125m, "approver-0", new[] { kept.Id });
        Assert.Equal(ReceivableStatus.PendingVoid, held.Status);

        await Service("treasury-3").TransmitAsync(run.Id);

        var released = (await _h.Ledger.GetAsync(FfsRunHarness.Tenant, held.Id))!;
        Assert.Equal((ReceivableStatus.Open, 125m), (released.Status, released.OutstandingAmount));
        Assert.Contains(released.Entries, e => e.Type == ReceivableEntryType.Activated);
        Assert.Single(await _h.LedgerStore.ListOutstandingAsync(FfsRunHarness.Tenant, Npi));
    }

    [Fact]
    public async Task A_held_receivable_can_be_released_by_a_person_with_a_reason()
    {
        var held = await _h.Ledger.RecordForwardBalanceAsync(FfsRunHarness.Tenant, Npi, "TP-A",
            new ReversalRun { Id = "rr-9", ReversalRunNumber = "RR-9", TenantId = FfsRunHarness.Tenant }, "env-rev-9", "R-9", 60m, "approver-0", new[] { "p-x" });

        await Assert.ThrowsAsync<ArgumentException>(() => _h.Ledger.ActivateAsync(FfsRunHarness.Tenant, held.Id, "finance-1", " "));
        var released = await _h.Ledger.ActivateAsync(FfsRunHarness.Tenant, held.Id, "finance-1", "file sent by hand on 2026-05-02, bank ref 123");

        Assert.Equal(ReceivableStatus.Open, released.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Ledger.ActivateAsync(FfsRunHarness.Tenant, held.Id, "finance-1", "again"));
    }

    [Fact]
    public async Task A_receivable_recovered_before_the_void_is_settled_by_reversing_the_recovery_and_recording_the_refund()
    {
        var (run, voided, _) = await PinnedRunAsync();
        var receivable = (await ReverseAsync(voided))!; // opened before receivables were held (Open)
        var later = LaterPayment(OtherNpi, 30m);
        var offsets = await _h.Ledger.ApplyRecoveriesAsync(FfsRunHarness.Tenant, later, "run-later", "PR-LATER", "approver-9");
        Assert.Equal(30m, Assert.Single(offsets).Amount);

        // Not refused: the file is not frozen for the other payees.
        var repinned = await Service("treasury-2").RedateAsync(run.Id, "payment reversed before transmission");

        var notice = Assert.Single((await _h.Runs.GetByIdAsync(run.Id))!.EftFile!.VoidedPayments);
        Assert.True(notice.Completed);
        Assert.False(notice.NeedsAttention);
        Assert.Contains(later.Id, Assert.Single(notice.RefundsOwed));
        var settled = (await _h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!;
        Assert.Equal((ReceivableStatus.Cancelled, 0m, 0m), (settled.Status, settled.OutstandingAmount, settled.RecoveredAmount));
        Assert.Contains(settled.Entries, e => e.Type == ReceivableEntryType.RecoveryReversed && e.PaymentId == later.Id);
        Assert.Contains((await _h.Runs.GetByIdAsync(run.Id))!.Warnings, w => w.Contains("owed that amount back"));
        // ...and an open item for the GL reconciliation, so the manual posting cannot be missed.
        var refundEvent = Assert.Single((await _h.Runs.GetByIdAsync(run.Id))!.GlOutbox, m => m.Type == GlEventTypes.ProviderRefundOwed);
        var refund = JsonSerializer.Deserialize<ProviderRefundOwedEvent>(refundEvent.PayloadJson, GlEventTypes.Json)!;
        Assert.Equal((30m, later.Id, receivable.Id, OtherNpi), (refund.Amount, refund.RecoveringPaymentId, refund.ReceivableId, refund.ProviderNpi));
        Assert.Equal(200m, repinned.TotalCreditAmount);
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service("treasury-3").TransmitAsync(run.Id)).Status);
    }

    [Fact]
    public async Task A_void_the_ledger_cannot_settle_needs_a_person_but_never_blocks_the_file()
    {
        var (run, voided, _) = await PinnedRunAsync();
        await ReverseAsync(voided);
        var stuck = new FlakyReceivables(_h.LedgerStore) { AlwaysConflict = true };

        await WithLedger("treasury-2", stuck).RedateAsync(run.Id, "payment reversed");
        var sent = await WithLedger("treasury-3", stuck).TransmitAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, sent.Status);
        var notice = Assert.Single((await _h.Runs.GetByIdAsync(run.Id))!.EftFile!.VoidedPayments);
        Assert.True(notice.Completed && notice.NeedsAttention);
        Assert.Contains("kept changing", notice.Problem);
        Assert.Contains((await _h.Runs.GetByIdAsync(run.Id))!.Warnings, w => w.Contains("needs a person"));
        Assert.NotNull((await _h.Payments.GetByIdAsync(voided.Id))!.VoidedBeforePaymentAt);
    }

    [Fact]
    public async Task Finance_can_exclude_a_partly_reversed_payment_and_its_unreversed_part_stays_payable()
    {
        var (run, _, kept) = await PinnedRunAsync();
        var reversedClaim = kept.ClaimPayments.Single(c => c.PaymentAmount == 125m).ClaimId;
        var receivable = (await ReverseAsync(kept, held: true, claimIds: reversedClaim))!;
        var refused = await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-2").RedateAsync(run.Id, "reversed"));
        Assert.Contains("exclude-payments", refused.Message);

        await Assert.ThrowsAsync<ArgumentException>(() => Service("treasury-2").ExcludePaymentsAsync(run.Id, Array.Empty<string>(), "x"));
        var file = await Service("treasury-2").ExcludePaymentsAsync(run.Id, new[] { kept.Id }, "c1 recouped; c2 to be reissued by check");

        Assert.Equal(50m, file.TotalCreditAmount); // the other payee is paid
        var notice = Assert.Single(file.VoidedPayments);
        Assert.Equal((kept.Id, 200m, 125m, 75m, true, false), (notice.PaymentId, notice.Amount, notice.PayableRelieved, notice.UnpaidRemainder,
            notice.ExcludedByOverride, notice.OffsetsReversed));
        Assert.Equal((ReceivableStatus.Cancelled, 0m), ((await _h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!.Status,
            (await _h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!.OutstandingAmount));
        var stored = (await _h.Runs.GetByIdAsync(run.Id))!;
        var gl = JsonSerializer.Deserialize<PaymentVoidedBeforeTransmissionEvent>(
            Assert.Single(stored.GlOutbox, m => m.Type == GlEventTypes.PaymentVoidedBeforeTransmission).PayloadJson, GlEventTypes.Json)!;
        Assert.Equal((125m, 75m, true), (gl.Amount, gl.UnpaidRemainder, gl.ExcludedByOverride));
        Assert.Contains(stored.Warnings, w => w.Contains("75.00 of it was not reversed") && w.Contains("reissued"));
        Assert.Contains("excluded by finance", Assert.Single(stored.EftFileHistory).SupersededReason);

        // Second person: the one who excluded it may not send the new file.
        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("treasury-2").TransmitAsync(run.Id));
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service("treasury-3").TransmitAsync(run.Id)).Status);

        // The 75 still owed is paid another way and recorded by a second person, once.
        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("treasury-2").ReissueRemainderAsync(run.Id, kept.Id, "Check", "CHK-1", "c2 owed"));
        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("maker-1").ReissueRemainderAsync(run.Id, kept.Id, "Check", "CHK-1", "c2 owed"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service("treasury-4").ReissueRemainderAsync(run.Id, kept.Id, "Check", " ", "c2 owed"));
        var reissued = await Service("treasury-4").ReissueRemainderAsync(run.Id, kept.Id, "Check", "CHK-55012", "c2 owed; paid by manual check");
        Assert.Equal(("treasury-4", "CHK-55012"), (reissued.RemainderReissuedBy, reissued.RemainderReissueReference));
        var after = (await _h.Runs.GetByIdAsync(run.Id))!;
        Assert.NotNull(Assert.Single(after.EftFile!.VoidedPayments).RemainderReissuedAt);
        var reissueEvent = JsonSerializer.Deserialize<VoidRemainderReissuedEvent>(
            Assert.Single(after.GlOutbox, m => m.Type == GlEventTypes.VoidRemainderReissued).PayloadJson, GlEventTypes.Json)!;
        Assert.Equal((75m, kept.Id, "CHK-55012"), (reissueEvent.Amount, reissueEvent.PaymentId, reissueEvent.Reference));
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-4").ReissueRemainderAsync(run.Id, kept.Id, "Check", "CHK-2", "again"));
    }

    [Fact]
    public async Task A_refund_owed_is_rebuilt_from_the_ledger_when_the_completion_crashed_after_reversing_the_recovery()
    {
        var (run, voided, _) = await PinnedRunAsync();
        var receivable = (await ReverseAsync(voided))!;
        var later = LaterPayment(OtherNpi, 30m);
        await _h.Ledger.ApplyRecoveriesAsync(FfsRunHarness.Tenant, later, "run-later", "PR-LATER", "approver-9");
        // The completion reverses the recovery and cancels the receivable, then crashes before the run is saved.
        _h.Runs.FailNextEftFileSave = new TimeoutException("database");

        await Service("treasury-2").RedateAsync(run.Id, "payment reversed before transmission");

        var unsaved = (await _h.Runs.GetByIdAsync(run.Id))!;
        Assert.False(Assert.Single(unsaved.EftFile!.VoidedPayments).Completed);
        Assert.DoesNotContain(unsaved.GlOutbox, m => m.Type == GlEventTypes.ProviderRefundOwed);
        Assert.Equal(ReceivableStatus.Cancelled, (await _h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!.Status);

        // The next completion has nothing left to reverse, but rebuilds the refund from the ledger.
        await Service("treasury-3").TransmitAsync(run.Id);

        var stored = (await _h.Runs.GetByIdAsync(run.Id))!;
        var notice = Assert.Single(stored.EftFile!.VoidedPayments);
        Assert.True(notice.Completed);
        Assert.Contains(later.Id, Assert.Single(notice.RefundsOwed));
        var refund = JsonSerializer.Deserialize<ProviderRefundOwedEvent>(
            Assert.Single(stored.GlOutbox, m => m.Type == GlEventTypes.ProviderRefundOwed).PayloadJson, GlEventTypes.Json)!;
        Assert.Equal((30m, later.Id), (refund.Amount, refund.RecoveringPaymentId));
    }

    [Fact]
    public async Task Finance_can_exclude_a_payment_that_recovered_receivables_and_its_recoveries_are_undone()
    {
        // The provider owed 10 from before: the run's payment to it recovers that (net 40).
        var old = await _h.SeedReceivableAsync(OtherNpi, 10m, envelopeId: "env-old");
        var (run, voided, _) = await PinnedRunAsync(expectedTotal: 240m);
        Assert.Equal((40m, 10m), (voided.TotalPaymentAmount, voided.ReceivableOffsets.Sum(o => o.Amount)));
        var reversal = (await ReverseAsync(voided, held: true))!;
        var refused = await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-2").RedateAsync(run.Id, "reversed"));
        Assert.Contains("recovered receivables", refused.Message);

        var file = await Service("treasury-2").ExcludePaymentsAsync(run.Id, new[] { voided.Id }, "reversed; its recovery never happened");

        var notice = Assert.Single(file.VoidedPayments);
        Assert.Equal((40m, 0m, true), (notice.PayableRelieved, notice.UnpaidRemainder, notice.OffsetsReversed));
        // The payment never went out: the old receivable is owed again, the reversal's is cancelled.
        Assert.Equal(10m, (await _h.Ledger.GetAsync(FfsRunHarness.Tenant, old.Id))!.OutstandingAmount);
        Assert.Equal(ReceivableStatus.Cancelled, (await _h.Ledger.GetAsync(FfsRunHarness.Tenant, reversal.Id))!.Status);
    }

    [Fact]
    public async Task Excluding_a_payment_without_a_completed_reversal_is_refused()
    {
        var (run, voided, kept) = await PinnedRunAsync();

        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-2").ExcludePaymentsAsync(run.Id, new[] { kept.Id }, "x"));
        await ReverseAsync(voided, complete: false, receivable: false);
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-2").ExcludePaymentsAsync(run.Id, new[] { voided.Id }, "x"));
        Assert.Null(await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile!.FileReference));
    }

    [Fact]
    public async Task A_file_whose_payments_are_all_unreversed_and_still_sendable_is_not_re_pinned()
    {
        var (run, _, _) = await PinnedRunAsync();

        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-2").RedateAsync(run.Id, "just because"));
    }

    private sealed class FlakyReceivables : IProviderReceivableRepository
    {
        private readonly IProviderReceivableRepository _inner;
        public bool FailNextReplace { get; set; }
        public bool AlwaysConflict { get; set; }
        public FlakyReceivables(IProviderReceivableRepository inner) => _inner = inner;
        public Task<ProviderReceivableRecord> CreateIfAbsentAsync(ProviderReceivableRecord record) => _inner.CreateIfAbsentAsync(record);
        public Task<ProviderReceivableRecord?> GetAsync(string tenantId, string id) => _inner.GetAsync(tenantId, id);
        public Task<IReadOnlyList<ProviderReceivableRecord>> ListOutstandingAsync(string tenantId, string providerNpi) => _inner.ListOutstandingAsync(tenantId, providerNpi);
        public Task<IReadOnlyList<ProviderReceivableRecord>> SearchAsync(string tenantId, string? providerNpi = null, ReceivableStatus? status = null)
            => _inner.SearchAsync(tenantId, providerNpi, status);
        public Task<bool> TryReplaceAsync(ProviderReceivableRecord record, long expectedVersion)
        {
            if (AlwaysConflict)
                return Task.FromResult(false);
            if (FailNextReplace)
            {
                FailNextReplace = false;
                throw new TimeoutException("ledger");
            }
            return _inner.TryReplaceAsync(record, expectedVersion);
        }
    }
}
