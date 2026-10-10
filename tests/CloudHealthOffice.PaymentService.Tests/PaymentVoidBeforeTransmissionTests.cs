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

    private async Task<(PaymentRun Run, Payment Voided, Payment Kept)> PinnedRunAsync()
    {
        var run = await _h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 125m), FfsRunHarness.Claim("c2", Npi, 75m), FfsRunHarness.Claim("c3", OtherNpi, 50m));
        var pinned = (await _h.EftFiles().GenerateAsync(run.Id, "approver-2")).Run;
        Assert.Equal(250m, pinned.EftFile!.TotalCreditAmount);
        var payments = _h.Payments.All.Where(p => p.RunId == run.Id && !p.IsReversal).ToList();
        return (pinned, payments.Single(p => p.PayeeNPI == OtherNpi), payments.First(p => p.PayeeNPI == Npi));
    }

    /// <summary>What a completed reversal run leaves behind for <paramref name="claimIds"/>: reservations, reversal payments in one 835, the receivable.</summary>
    private async Task<ProviderReceivableRecord?> ReverseAsync(Payment original, string reversalRunId = "rr-1", string envelopeId = "env-rev-1",
        bool complete = true, bool receivable = true, params string[] claimIds)
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
        return await _h.SeedReceivableAsync(original.PayeeNPI!, owed, envelopeId: envelopeId);
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
        // The re-pin lands but the cancellation of the receivable fails once (the ledger is unreachable).
        var flaky = new FlakyReceivables(_h.LedgerStore) { FailNextReplace = true };
        var ledger = new ProviderReceivableLedger(flaky, _h.Configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderReceivableLedger>.Instance);
        var redater = new PaymentFileTransmissionService(_h.Runs, _h.Payments, _h.Reservations, _h.EftFiles(), _store, _bank, _bank,
            new TestActor("treasury-2", FfsRunHarness.Tenant).SeparationOfDuties(), Options.Create(_options),
            new AchEffectiveDatePolicy(Options.Create(_options), _clock), _log, _clock, ledger);

        await Assert.ThrowsAsync<TimeoutException>(() => redater.RedateAsync(run.Id, "payment reversed"));

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

    [Fact]
    public async Task A_receivable_already_partly_recovered_is_refused()
    {
        var (run, voided, _) = await PinnedRunAsync();
        var receivable = (await ReverseAsync(voided))!;
        // A later run already recovered part of it from the provider.
        var current = (await _h.LedgerStore.GetAsync(FfsRunHarness.Tenant, receivable.Id))!;
        var version = current.Version;
        current.OutstandingAmount = 20m;
        current.RecoveredAmount = 30m;
        current.Version = version + 1;
        Assert.True(await _h.LedgerStore.TryReplaceAsync(current, version));

        var ex = await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-2").RedateAsync(run.Id, "reversed"));

        Assert.Contains("only 20.00 outstanding", ex.Message);
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
        public FlakyReceivables(IProviderReceivableRepository inner) => _inner = inner;
        public Task<ProviderReceivableRecord> CreateIfAbsentAsync(ProviderReceivableRecord record) => _inner.CreateIfAbsentAsync(record);
        public Task<ProviderReceivableRecord?> GetAsync(string tenantId, string id) => _inner.GetAsync(tenantId, id);
        public Task<IReadOnlyList<ProviderReceivableRecord>> ListOutstandingAsync(string tenantId, string providerNpi) => _inner.ListOutstandingAsync(tenantId, providerNpi);
        public Task<IReadOnlyList<ProviderReceivableRecord>> SearchAsync(string tenantId, string? providerNpi = null, ReceivableStatus? status = null)
            => _inner.SearchAsync(tenantId, providerNpi, status);
        public Task<bool> TryReplaceAsync(ProviderReceivableRecord record, long expectedVersion)
        {
            if (FailNextReplace)
            {
                FailNextReplace = false;
                throw new TimeoutException("ledger");
            }
            return _inner.TryReplaceAsync(record, expectedVersion);
        }
    }
}
