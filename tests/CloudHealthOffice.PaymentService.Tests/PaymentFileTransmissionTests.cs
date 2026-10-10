using System.Collections.Concurrent;
using System.Text.Json;
using CloudHealthOffice.NachaTransmission;
using CloudHealthOffice.PaymentService.Tests.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using PaymentService.Services;
using Renci.SshNet.Common;
using Renci.SshNet.Security;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// Exactly-once bank transmission of a payment run's NACHA file: one record per
/// file pinned to its SHA-256, a conditional claim before any send, no re-send
/// after an unknown outcome until reconciled or resolved, the maker-checker
/// rule, the off switch, and the pinned host key. Nothing here connects
/// anywhere: the bank is in memory.
/// </summary>
public class PaymentFileTransmissionTests
{
    private const string Npi = "1111111111";
    private const string Account = "111122223333";
    private const string TinDigits = "123456789";

    private readonly FfsRunHarness _h = new();
    private readonly InMemoryPaymentFileTransmissionRepository _store = new();
    private readonly InMemoryBank _bank = new();
    private readonly CapturingLogger<PaymentFileTransmissionService> _log = new();
    private readonly MutableClock _clock = new();
    private readonly BankTransmissionOptions _options = new() { Enabled = true };

    public PaymentFileTransmissionTests()
    {
        _h.Partner(Npi, "TP-A");
        _h.Accounts.Eft(Npi, account: Account, tin: "12-3456789");
        _h.Clock = _clock;
        _h.DateOptions = _options;
    }

    private PaymentFileTransmissionService Service(string user = "treasury-1", bool isService = false,
        INachaTransmitter? transmitter = null, INachaRemoteFileProbe? probe = null, IFfsEftFileService? eftFiles = null)
        => new(_h.Runs, _h.Payments, _h.Reservations, eftFiles ?? _h.EftFiles(), _store, transmitter ?? _bank, probe ?? _bank,
            new TestActor(user, FfsRunHarness.Tenant, isService).SeparationOfDuties(),
            Options.Create(_options), new AchEffectiveDatePolicy(Options.Create(_options), _clock), _log, _clock);

    /// <summary>An executed ACH run (created by maker-1) whose EFT file was generated and pinned.</summary>
    private async Task<PaymentRun> PinnedRunAsync()
    {
        var run = await _h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 125m), FfsRunHarness.Claim("c2", Npi, 75m));
        var outcome = await _h.EftFiles().GenerateAsync(run.Id, "approver-2");
        Assert.NotNull(outcome.File);
        return outcome.Run;
    }

    [Fact]
    public async Task Happy_path_sends_the_approved_bytes_once_and_records_the_audit_and_the_gl_event()
    {
        var run = await PinnedRunAsync();

        var record = await Service().TransmitAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, record.Status);
        Assert.Equal(PaymentFileDeliveryEvidence.Upload, record.ConfirmedBy);
        Assert.Equal((1, "treasury-1", "treasury-1"), (record.AttemptCount, record.ApprovedBy, record.TransmittedBy));
        Assert.Equal(run.EftFile!.Sha256, record.ApprovedSha256);
        Assert.Equal(PaymentFileAcknowledgementStatus.Awaiting, record.Acknowledgement);

        var sent = Assert.Single(_bank.Sent);
        Assert.Equal(run.EftFile.FileName, sent.FileName);
        Assert.Equal(record.ApprovedSha256, NachaFileFacts.From(sent.Content).Sha256);
        Assert.Equal("treasury-1", sent.TransmittedBy);

        // Every action audited: operator, file hash, result.
        Assert.Collection(record.Attempts,
            a => Assert.Equal((PaymentFileTransmissionAction.Transmit, "treasury-1", record.ApprovedSha256, PaymentFileTransmissionStatus.Transmitting),
                (a.Action, a.By, a.Sha256, a.Result)),
            a => Assert.Equal((PaymentFileTransmissionAction.Transmit, "treasury-1", record.ApprovedSha256, PaymentFileTransmissionStatus.Transmitted),
                (a.Action, a.By, a.Sha256, a.Result)));

        // The GL-posting seam: one PaymentFileTransmitted event in the record's outbox.
        var message = Assert.Single(record.Outbox);
        Assert.Equal(PaymentFileOutboxMessage.TransmittedType, message.Type);
        Assert.Null(message.PublishedAt);
        var payload = JsonSerializer.Deserialize<CloudHealthOffice.Finance.Contracts.PaymentFileTransmittedEvent>(message.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal((run.Id, 200m, 1, record.ApprovedSha256, "Upload"),
            (payload.PaymentRunId, payload.TotalCreditAmount, payload.EntryCount, payload.Sha256, payload.ConfirmedBy));
        Assert.Equal(message.EventId, payload.EventId);
        Assert.Equal(run.EftFile.EffectiveEntryDate, payload.EffectiveEntryDate);
        Assert.Equal(new DateTime(2026, 5, 4), payload.EffectiveEntryDate); // Fri May 1 -> next banking day, Mon May 4

        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile.FileReference))!.Status);
        AssertNoBankNumbers();
    }

    [Fact]
    public async Task A_transmitted_file_is_never_sent_again()
    {
        var run = await PinnedRunAsync();
        await Service().TransmitAsync(run.Id);

        var again = await Service("treasury-3").TransmitAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, again.Status);
        Assert.Single(_bank.Sent);
        Assert.Single(again.Outbox);
        Assert.Equal(1, again.AttemptCount);
    }

    [Fact]
    public async Task A_transient_failure_is_Failed_and_a_retry_sends_it()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(InMemoryBank.UploadFails);

        var failed = await Service().TransmitAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.Failed, failed.Status);
        Assert.Contains("upload", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(failed.Outbox);
        Assert.Empty(_bank.Drop);

        var retried = await Service("treasury-3").TransmitAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, retried.Status);
        Assert.Equal(2, retried.AttemptCount);
        Assert.Equal(2, _bank.Sent.Count);
        Assert.Single(_bank.Drop);
        Assert.Equal("treasury-1", retried.ApprovedBy);
        Assert.Equal("treasury-3", retried.TransmittedBy);
        Assert.Equal(new[] { PaymentFileTransmissionStatus.Transmitting, PaymentFileTransmissionStatus.Failed,
                             PaymentFileTransmissionStatus.Transmitting, PaymentFileTransmissionStatus.Transmitted },
            retried.Attempts.Select(a => a.Result));
        Assert.Single(retried.Outbox);
    }

    [Fact]
    public async Task An_ambiguous_failure_parks_the_file_as_NeedsReview_and_it_is_never_sent_twice()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(_bank.RenameReplyLost);

        var record = await Service().TransmitAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.NeedsReview, record.Status);
        Assert.Empty(record.Outbox);

        // Neither the approver nor anyone else can simply send it again.
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service().TransmitAsync(run.Id));
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3").TransmitAsync(run.Id));

        Assert.Single(_bank.Sent);
        Assert.Equal(1, (await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile!.FileReference))!.AttemptCount);
    }

    [Fact]
    public async Task Reconciling_finds_the_file_in_the_drop_and_settles_it_without_sending()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(_bank.RenameReplyLost); // the file did land
        await Service().TransmitAsync(run.Id);

        var reconciled = await Service("treasury-3").ReconcileAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, reconciled.Status);
        Assert.Equal(PaymentFileDeliveryEvidence.RemoteListing, reconciled.ConfirmedBy);
        Assert.Equal("treasury-1", reconciled.TransmittedBy);
        Assert.Single(_bank.Sent);
        Assert.Equal(1, _bank.Probes);
        Assert.Equal(PaymentFileTransmissionAction.Reconcile, reconciled.Attempts.Last().Action);
        var message = Assert.Single(reconciled.Outbox);
        Assert.Contains("\"confirmedBy\":\"RemoteListing\"", message.PayloadJson);
    }

    [Fact]
    public async Task Reconciling_when_the_file_is_not_in_the_drop_keeps_it_NeedsReview_and_sends_nothing()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(InMemoryBank.AmbiguousNothingLanded);
        await Service().TransmitAsync(run.Id);

        var record = await Service("treasury-3").ReconcileAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.NeedsReview, record.Status);
        Assert.Contains("may already have collected it", record.Attempts.Last().Detail);
        Assert.Single(_bank.Sent);
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3").TransmitAsync(run.Id));
        Assert.Single(_bank.Sent);
    }

    [Fact]
    public async Task A_file_of_the_same_name_but_another_size_is_not_proof_of_delivery()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(InMemoryBank.AmbiguousNothingLanded);
        await Service().TransmitAsync(run.Id);
        _bank.Drop[run.EftFile!.FileName] = new byte[] { 1, 2, 3 };

        var record = await Service("treasury-3").ReconcileAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.NeedsReview, record.Status);
        Assert.Empty(record.Outbox);
    }

    [Fact]
    public async Task A_second_user_records_the_banks_answer_and_only_then_may_it_be_retried()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(InMemoryBank.AmbiguousNothingLanded);
        await Service().TransmitAsync(run.Id);

        // The approver (who also attempted it) and the run's maker may not settle it.
        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("treasury-1").ResolveAsync(run.Id, false, "bank says no"));
        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("maker-1").ResolveAsync(run.Id, false, "bank says no"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service("treasury-3").ResolveAsync(run.Id, false, " "));

        var resolved = await Service("treasury-3").ResolveAsync(run.Id, false, "ACH ops (J. Doe) confirmed no file FFS received, ticket 4411");

        Assert.Equal(PaymentFileTransmissionStatus.Failed, resolved.Status);
        Assert.Equal((PaymentFileTransmissionAction.Resolve, "treasury-3"), (resolved.Attempts.Last().Action, resolved.Attempts.Last().By));

        var retried = await Service("treasury-1").TransmitAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, retried.Status);
        Assert.Equal(2, _bank.Sent.Count);
    }

    [Fact]
    public async Task The_bank_confirming_receipt_settles_it_as_Transmitted_with_one_event()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(InMemoryBank.AmbiguousNothingLanded);
        await Service().TransmitAsync(run.Id);

        var resolved = await Service("treasury-3").ResolveAsync(run.Id, true, "bank confirmed file received and collected 10:42, ref 9981");

        Assert.Equal((PaymentFileTransmissionStatus.Transmitted, PaymentFileDeliveryEvidence.BankConfirmation), (resolved.Status, resolved.ConfirmedBy));
        Assert.Single(resolved.Outbox);
        Assert.Equal(resolved, await Service("treasury-4").TransmitAsync(run.Id), new SameStatus());
        Assert.Single(_bank.Sent);
    }

    [Fact]
    public async Task An_unexpected_exception_from_the_transmitter_is_an_unknown_outcome()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(_ => throw new TimeoutException("socket"));

        var record = await Service().TransmitAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.NeedsReview, record.Status);
        Assert.Contains("TimeoutException", record.Reason);
    }

    [Fact]
    public async Task An_attempt_that_outlived_its_lease_is_NeedsReview_and_not_resent()
    {
        var run = await PinnedRunAsync();
        _store.Put(Record(run, PaymentFileTransmissionStatus.Transmitting, leaseUntil: _clock.Now.UtcDateTime.AddMinutes(-1)));

        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service().TransmitAsync(run.Id));

        var record = (await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile!.FileReference))!;
        Assert.Equal(PaymentFileTransmissionStatus.NeedsReview, record.Status);
        Assert.Equal(PaymentFileTransmissionAction.LeaseExpired, record.Attempts.Last().Action);
        Assert.Empty(_bank.Sent);
    }

    [Fact]
    public async Task An_attempt_in_progress_blocks_a_second_one()
    {
        var run = await PinnedRunAsync();
        var gate = new TaskCompletionSource();
        var inside = new TaskCompletionSource();
        _bank.BeforeSend = async () => { inside.TrySetResult(); await gate.Task; };

        var first = Service().TransmitAsync(run.Id);
        await inside.Task;
        _bank.BeforeSend = null;
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3").TransmitAsync(run.Id));
        gate.SetResult();

        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await first).Status);
        Assert.Single(_bank.Sent);
    }

    [Fact]
    public async Task Concurrent_first_approvals_send_once()
    {
        var run = await PinnedRunAsync();
        var gate = new TaskCompletionSource();
        _bank.BeforeSend = () => gate.Task;

        var attempts = Enumerable.Range(0, 4).Select(i => Task.Run(() => Service("treasury-" + (10 + i)).TransmitAsync(run.Id))).ToList();
        await Task.Delay(200);
        gate.SetResult();
        var outcomes = await Task.WhenAll(attempts.Select(async t =>
        {
            try { return (await t).Status.ToString(); }
            catch (PaymentFileTransmissionStateException) { return "refused"; }
        }));

        Assert.Single(_bank.Sent);
        Assert.Single(outcomes, o => o == "Transmitted");
    }

    [Fact]
    public async Task The_outcome_not_recorded_after_a_send_never_leads_to_a_second_send()
    {
        var run = await PinnedRunAsync();
        _bank.AfterSend = () => _store.FailNextReplace = new TimeoutException("database");

        await Assert.ThrowsAsync<TimeoutException>(() => Service().TransmitAsync(run.Id));
        Assert.Equal(PaymentFileTransmissionStatus.Transmitting, (await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile!.FileReference))!.Status);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Critical);

        // Within the lease: refused. After it: NeedsReview, reconciled from the drop.
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3").TransmitAsync(run.Id));
        _clock.Now = _clock.Now.AddMinutes(16);
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3").TransmitAsync(run.Id));
        var reconciled = await Service("treasury-3").ReconcileAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, reconciled.Status);
        Assert.Single(_bank.Sent);
    }

    [Fact]
    public async Task A_file_whose_content_changed_after_approval_is_refused()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(InMemoryBank.UploadFails);
        await Service().TransmitAsync(run.Id);

        // The payee's approved account changes after approval: the file would differ.
        _h.Accounts.Eft(Npi, account: "999988887777", tin: "12-3456789");

        await Assert.ThrowsAsync<PaymentFileHashMismatchException>(() => Service("treasury-3").TransmitAsync(run.Id));

        Assert.Single(_bank.Sent);
        var record = (await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile!.FileReference))!;
        Assert.Equal(PaymentFileTransmissionStatus.Failed, record.Status);
        Assert.Equal((PaymentFileTransmissionAction.Refused, "treasury-3"), (record.Attempts.Last().Action, record.Attempts.Last().By));
    }

    [Fact]
    public async Task A_record_approved_for_other_bytes_is_refused()
    {
        var run = await PinnedRunAsync();
        var tampered = Record(run, PaymentFileTransmissionStatus.Pending);
        tampered.ApprovedSha256 = new string('a', 64);
        _store.Put(tampered);

        await Assert.ThrowsAsync<PaymentFileHashMismatchException>(() => Service().TransmitAsync(run.Id));

        Assert.Empty(_bank.Sent);
        Assert.Equal(PaymentFileTransmissionAction.Refused,
            (await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile!.FileReference))!.Attempts.Last().Action);
    }

    [Fact]
    public async Task Disabled_does_nothing_at_all()
    {
        var run = await PinnedRunAsync();
        _options.Enabled = false;
        var accountReads = _h.Accounts.Requests.Count;

        await Assert.ThrowsAsync<BankTransmissionDisabledException>(() => Service().TransmitAsync(run.Id));
        await Assert.ThrowsAsync<BankTransmissionDisabledException>(() => Service().ReconcileAsync(run.Id));

        Assert.Empty(_bank.Sent);
        Assert.Equal(0, _bank.Probes);
        Assert.Empty(_store.All);
        Assert.Equal(accountReads, _h.Accounts.Requests.Count); // the file was not even regenerated
        Assert.Null(await Service().GetAsync(run.Id));
    }

    [Fact]
    public async Task The_disabled_transmitter_registered_while_off_connects_nowhere()
    {
        var off = new DisabledNachaTransmitter();
        var ex = await Assert.ThrowsAsync<NachaTransmissionException>(() => off.TransmitAsync(new NachaTransmissionRequest
        {
            TenantId = "t", FileReference = "f", FileName = "f.ach", Content = "x", TransmittedBy = "u",
        }));
        Assert.True(ex.NotConfigured);
        await Assert.ThrowsAsync<NachaTransmissionException>(() => off.CheckAsync("t", "f.ach", 1));
    }

    [Fact]
    public async Task The_runs_maker_and_a_service_token_cannot_approve_transmission()
    {
        var run = await PinnedRunAsync();

        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("maker-1").TransmitAsync(run.Id));
        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("payment-scheduler", isService: true).TransmitAsync(run.Id));

        Assert.Empty(_bank.Sent);
        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task A_run_without_a_pinned_file_is_refused()
    {
        var run = await _h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 125m));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service().TransmitAsync(run.Id));

        Assert.Contains("no EFT file yet", ex.Message);
        Assert.Empty(_bank.Sent);
    }

    [Fact]
    public async Task A_host_key_that_is_not_the_pinned_one_is_refused_before_anything_is_uploaded()
    {
        var run = await PinnedRunAsync();
        var bankPin = HostKeyPin.Of(InMemorySftp.Key(1).HostKey);
        var sftp = new InMemorySftp(presentedSeed: 77); // someone else answers
        var transmitter = InMemorySftp.Transmitter(sftp, pin: bankPin);

        var record = await Service(transmitter: transmitter, probe: transmitter).TransmitAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.Failed, record.Status);
        Assert.Contains("host key refused", record.Reason);
        Assert.Contains("not the pinned key", record.Reason);
        Assert.Empty(sftp.Files);
        Assert.Equal(0, sftp.Sessions);
        Assert.Empty(record.Outbox);

        // Once the bank's real key answers, the retry goes through.
        var genuine = InMemorySftp.Transmitter(new InMemorySftp(presentedSeed: 1), pin: bankPin);
        var retried = await Service("treasury-3", transmitter: genuine, probe: genuine).TransmitAsync(run.Id);
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, retried.Status);
    }

    [Fact]
    public async Task Through_the_real_sftp_transmitter_a_lost_rename_reply_is_reconciled_from_the_listing()
    {
        var run = await PinnedRunAsync();
        var sftp = new InMemorySftp(presentedSeed: 1) { RenameReplyLostAndConnectionDies = true };
        var transmitter = InMemorySftp.Transmitter(sftp, pin: HostKeyPin.Of(InMemorySftp.Key(1).HostKey));

        var record = await Service(transmitter: transmitter, probe: transmitter).TransmitAsync(run.Id);
        Assert.Equal(PaymentFileTransmissionStatus.NeedsReview, record.Status);

        sftp.RenameReplyLostAndConnectionDies = false;
        var reconciled = await Service("treasury-3", transmitter: transmitter, probe: transmitter).ReconcileAsync(run.Id);

        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, reconciled.Status);
        Assert.Equal(1, sftp.Uploads);
        Assert.Equal("sftp://sftp.bank.example:22/inbound/ach", reconciled.Destination);
    }

    [Fact]
    public async Task Logs_and_records_carry_no_account_or_tax_id_numbers()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(InMemoryBank.UploadFails);
        await Service().TransmitAsync(run.Id);
        await Service("treasury-3").TransmitAsync(run.Id);

        AssertNoBankNumbers();
        Assert.Contains(_log.Entries, e => e.Message.StartsWith("AUDIT") && e.Message.Contains("treasury-3") && e.Message.Contains(run.EftFile!.Sha256));
    }

    [Fact]
    public async Task The_runs_executor_cannot_send_its_file_either()
    {
        var run = await PinnedRunAsync();
        Assert.Equal("approver-1", run.ExecutedBy);

        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("approver-1").TransmitAsync(run.Id));

        Assert.Empty(_bank.Sent);
        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task A_run_without_a_recorded_creator_is_refused()
    {
        var run = await PinnedRunAsync();
        _h.Runs.Mutate(run.Id, r => r.CreatedBy = null);

        var ex = await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service().TransmitAsync(run.Id));

        Assert.Contains("no recorded creator", ex.Message);
        Assert.Empty(_bank.Sent);
    }

    [Fact]
    public async Task Whoever_reconciled_may_not_record_the_banks_answer()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(InMemoryBank.AmbiguousNothingLanded);
        await Service().TransmitAsync(run.Id);
        await Service("treasury-3").ReconcileAsync(run.Id);

        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("treasury-3").ResolveAsync(run.Id, false, "bank says no"));

        Assert.Equal(PaymentFileTransmissionStatus.Failed, (await Service("treasury-4").ResolveAsync(run.Id, false, "bank says no")).Status);
    }

    [Fact]
    public async Task A_delivery_that_finishes_after_its_lease_was_expired_and_resolved_forces_NeedsReview()
    {
        var run = await PinnedRunAsync();
        var gate = new TaskCompletionSource();
        var inside = new TaskCompletionSource();
        _bank.BeforeSend = async () => { inside.TrySetResult(); await gate.Task; };

        var slow = Service().TransmitAsync(run.Id);
        await inside.Task;
        _bank.BeforeSend = null;

        // The attempt hangs past its lease; another user parks it, a third records the
        // bank's (premature) "not received".
        _clock.Now = _clock.Now.AddMinutes(16);
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3").TransmitAsync(run.Id));
        Assert.Equal(PaymentFileTransmissionStatus.Failed,
            (await Service("treasury-4").ResolveAsync(run.Id, false, "bank sees nothing yet")).Status);

        // Then the hung upload completes: the file is at the bank.
        gate.SetResult();
        var late = await slow;

        Assert.Equal(PaymentFileTransmissionStatus.NeedsReview, late.Status);
        var stored = (await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile!.FileReference))!;
        Assert.Equal(PaymentFileTransmissionStatus.NeedsReview, stored.Status);
        Assert.Contains("late delivery evidence", stored.Reason);
        Assert.Equal(PaymentFileTransmissionAction.LateOutcome, stored.Attempts.Last().Action);
        Assert.Empty(stored.Outbox);
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3").TransmitAsync(run.Id));
        Assert.Single(_bank.Sent);

        // The listing then settles it.
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service("treasury-3").ReconcileAsync(run.Id)).Status);
    }

    [Fact]
    public async Task A_file_whose_effective_entry_date_has_passed_is_not_sent()
    {
        var run = await PinnedRunAsync();
        _clock.Now = new DateTimeOffset(run.EftFile!.EffectiveEntryDate.AddDays(1).AddHours(15), TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<PaymentFileApprovalStaleException>(() => Service().TransmitAsync(run.Id));

        Assert.Contains("is not after today", ex.Message);
        Assert.Empty(_bank.Sent);
        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task A_retry_after_the_effective_entry_date_is_refused_and_audited()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(InMemoryBank.UploadFails);
        await Service().TransmitAsync(run.Id);

        _clock.Now = new DateTimeOffset(run.EftFile!.EffectiveEntryDate.AddHours(15), TimeSpan.Zero); // the day itself: no same-day by default

        await Assert.ThrowsAsync<PaymentFileApprovalStaleException>(() => Service("treasury-3").TransmitAsync(run.Id));

        Assert.Single(_bank.Sent);
        var record = (await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile.FileReference))!;
        Assert.Equal((PaymentFileTransmissionAction.Refused, PaymentFileTransmissionStatus.Failed), (record.Attempts.Last().Action, record.Status));

        // Same-day ACH, when explicitly allowed, accepts the day itself.
        _options.AllowSameDayEffectiveDate = true;
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service("treasury-3").TransmitAsync(run.Id)).Status);
    }

    private void At(int year, int month, int day) => _clock.Now = new DateTimeOffset(year, month, day, 15, 0, 0, TimeSpan.Zero);

    /// <summary>A run created and executed through the real service (no injected payment date), its file pinned.</summary>
    private async Task<PaymentRun> RealRunAsync(DateTime? requested = null)
    {
        var run = await _h.CreateAndExecuteRunAsync(requested, FfsRunHarness.Claim("c1", Npi, 125m), FfsRunHarness.Claim("c2", Npi, 75m));
        return (await _h.EftFiles().GenerateAsync(run.Id, "approver-2")).Run;
    }

    [Theory]
    [InlineData(2026, 5, 6, 2026, 5, 7)]   // Wednesday: next banking day Thursday (the old +3 default was a Saturday)
    [InlineData(2026, 5, 7, 2026, 5, 8)]   // Thursday: Friday (old default: Sunday)
    [InlineData(2026, 5, 8, 2026, 5, 11)]  // Friday: Monday
    [InlineData(2026, 5, 22, 2026, 5, 26)] // Friday before Memorial Day: Tuesday (old default: the holiday)
    [InlineData(2026, 11, 25, 2026, 11, 27)] // day before Thanksgiving: Friday
    public async Task A_run_created_with_the_real_defaults_gets_a_banking_day_and_sends(int y, int m, int d, int ey, int em, int ed)
    {
        At(y, m, d);

        var run = await RealRunAsync();

        Assert.False(run.PaymentDateRequested);
        Assert.Equal(new DateTime(ey, em, ed), run.PaymentDate.Date);
        Assert.Equal(new DateTime(ey, em, ed), run.EftFile!.EffectiveEntryDate.Date);
        Assert.True(AchBankingCalendar.IsBankingDay(run.EftFile.EffectiveEntryDate));
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service().TransmitAsync(run.Id)).Status);
        Assert.Single(_bank.Sent);
    }

    private string Bpr16(PaymentRun run)
        => _h.Envelopes.Where(e => e.PaymentRunId == run.Id)
            .Select(e => FfsRunHarness.Segments(e).Single(s => s[0] == "BPR")[16])
            .Distinct().Single();

    [Fact]
    public async Task Bpr16_equals_the_file_date_for_a_default_run()
    {
        At(2026, 5, 6); // Wednesday
        var run = await RealRunAsync();

        Assert.Equal("20260507", Bpr16(run));
        Assert.Equal(run.EftFile!.EffectiveEntryDate.ToString("yyyyMMdd"), Bpr16(run));
    }

    [Fact]
    public async Task Bpr16_equals_the_file_date_for_a_requested_weekend_date()
    {
        At(2026, 5, 4);
        var created = await _h.Service().CreatePaymentRunAsync(new PaymentRunCriteria { GroupByProvider = true }, "maker-1",
            new DateTime(2026, 5, 16)); // a Saturday: rolled at create
        Assert.Equal(new DateTime(2026, 5, 18), created.PaymentDate.Date);

        var run = await _h.ExecuteCreatedRunAsync(created.Id, FfsRunHarness.Claim("c1", Npi, 125m));
        var pinned = (await _h.EftFiles().GenerateAsync(run.Id, "approver-2")).Run;

        Assert.Equal(("20260518", "20260518"), (Bpr16(run), pinned.EftFile!.EffectiveEntryDate.ToString("yyyyMMdd")));
    }

    [Fact]
    public async Task Bpr16_equals_the_file_date_when_the_run_is_executed_days_after_creation()
    {
        At(2026, 5, 4);
        var created = await _h.Service().CreatePaymentRunAsync(new PaymentRunCriteria { GroupByProvider = true }, "maker-1");
        Assert.Equal(new DateTime(2026, 5, 5), created.PaymentDate.Date);

        At(2026, 5, 12); // executed a week later: the date is fixed at execution
        var run = await _h.ExecuteCreatedRunAsync(created.Id, FfsRunHarness.Claim("c1", Npi, 125m));
        var pinned = (await _h.EftFiles().GenerateAsync(run.Id, "approver-2")).Run;

        Assert.Equal(new DateTime(2026, 5, 13), run.PaymentDate.Date);
        Assert.Equal(("20260513", "20260513"), (Bpr16(run), pinned.EftFile!.EffectiveEntryDate.ToString("yyyyMMdd")));
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service().TransmitAsync(run.Id)).Status);
    }

    [Fact]
    public async Task A_file_pinned_after_its_date_passed_is_refused_and_re_dating_records_the_835_date_notice()
    {
        At(2026, 5, 4);
        var run = await _h.CreateAndExecuteRunAsync(null, FfsRunHarness.Claim("c1", Npi, 125m));
        At(2026, 5, 12); // the file is generated (and pinned) only now, with the run's (835) date
        var pinned = (await _h.EftFiles().GenerateAsync(run.Id, "approver-2")).Run;
        Assert.Equal(("20260505", "20260505"), (Bpr16(run), pinned.EftFile!.EffectiveEntryDate.ToString("yyyyMMdd")));
        await Assert.ThrowsAsync<PaymentFileApprovalStaleException>(() => Service().TransmitAsync(run.Id));

        var redated = await Service("treasury-2").RedateAsync(run.Id, "pinned after its date");

        // The 835s are not rewritten: each is listed with its BPR16 and the new date.
        Assert.Equal(new DateTime(2026, 5, 13), redated.EffectiveEntryDate.Date);
        var notice = Assert.Single(redated.RemittanceDateNotices);
        Assert.Equal((new DateTime(2026, 5, 5), new DateTime(2026, 5, 13), "TP-A"), (notice.Bpr16Date.Date, notice.EffectiveEntryDate.Date, notice.TradingPartnerId));
        Assert.Equal("20260505", Bpr16(run));
        Assert.Contains((await _h.Runs.GetByIdAsync(run.Id))!.Warnings, w => w.Contains("carry BPR16 2026-05-05") && w.Contains("2026-05-13"));

        // ...and surfaced on the new file's transmission record.
        var sent = await Service().TransmitAsync(run.Id);
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, sent.Status);
        Assert.Single(sent.RemittanceDateNotices);
        Assert.Single((await Service().GetAsync(run.Id))!.RemittanceDateNotices);
    }

    [Fact]
    public async Task A_failed_write_of_the_fixed_payment_date_fails_the_run_instead_of_leaving_it_Running()
    {
        At(2026, 5, 4);
        var created = await _h.Service().CreatePaymentRunAsync(new PaymentRunCriteria { GroupByProvider = true }, "maker-1");
        _h.Runs.FailNextUpdate = new TimeoutException("database");

        await Assert.ThrowsAsync<TimeoutException>(() => _h.ExecuteCreatedRunAsync(created.Id, FfsRunHarness.Claim("c1", Npi, 125m)));

        var stored = (await _h.Runs.GetByIdAsync(created.Id))!;
        Assert.Equal(PaymentRunStatus.Failed, stored.Status);
        Assert.Contains(stored.Errors, e => e.Contains("database"));
        Assert.Empty(_h.Payments.All);
        Assert.Empty(_h.Envelopes);
    }

    [Fact]
    public async Task The_835_date_warning_of_a_re_date_survives_a_concurrent_write_to_the_run()
    {
        At(2026, 5, 4);
        var run = await _h.CreateAndExecuteRunAsync(null, FfsRunHarness.Claim("c1", Npi, 125m));
        At(2026, 5, 12);
        await _h.EftFiles().GenerateAsync(run.Id, "approver-2");
        // Between the re-pin's read of the run and its write, someone else writes the run.
        _h.Runs.AfterGet = () =>
        {
            _h.Runs.AfterGet = null;
            _h.Runs.Mutate(run.Id, r => r.Warnings.Add("concurrent finalize retry"));
            return Task.CompletedTask;
        };

        await Service("treasury-2").RedateAsync(run.Id, "pinned after its date");

        var warnings = (await _h.Runs.GetByIdAsync(run.Id))!.Warnings;
        Assert.Contains("concurrent finalize retry", warnings);
        Assert.Single(warnings, w => w.Contains("carry BPR16 2026-05-05"));
    }

    [Fact]
    public async Task The_re_dater_may_not_approve_the_new_file()
    {
        At(2026, 5, 4);
        var run = await RealRunAsync();
        At(2026, 5, 7);
        await Service("treasury-2").RedateAsync(run.Id, "late");

        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("treasury-2").TransmitAsync(run.Id));

        Assert.Empty(_bank.Sent);
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service("treasury-3").TransmitAsync(run.Id)).Status);
    }

    [Fact]
    public async Task A_concurrent_re_date_never_supersedes_the_file_another_re_date_pinned()
    {
        At(2026, 5, 4);
        var run = await RealRunAsync();
        var original = run.EftFile!;
        At(2026, 5, 7);

        // B checks the original file, then A re-dates it, R1 is approved and sent,
        // and only then does B's re-pin run.
        var b = new InterleavedEftFiles(_h.EftFiles(), async () =>
        {
            await Service("treasury-2").RedateAsync(run.Id, "A");
            Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service("treasury-3").TransmitAsync(run.Id)).Status);
        });

        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-4", eftFiles: b).RedateAsync(run.Id, "B"));

        var stored = (await _h.Runs.GetByIdAsync(run.Id))!;
        Assert.Equal((original.FileReference + "-R1", 1), (stored.EftFile!.FileReference, stored.EftFileHistory.Count));
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await _store.GetAsync(FfsRunHarness.Tenant, original.FileReference + "-R1"))!.Status);
        Assert.Single(_bank.Sent);

        // And directly: a re-pin naming a file that is no longer pinned is refused.
        await Assert.ThrowsAsync<RunConflictException>(() =>
            _h.EftFiles().RepinAsync(run.Id, "treasury-4", "stale", original.FileReference, original.Sha256));
    }

    [Fact]
    public async Task Re_dating_a_failed_file_found_in_the_drop_is_refused_and_audited()
    {
        At(2026, 5, 4);
        var run = await RealRunAsync();
        // The upload "failed", but the file did land (e.g. a server that reported an error after writing it).
        _bank.Script.Enqueue(r =>
        {
            _bank.Drop[r.FileName] = NachaFileFacts.Encode(r.Content);
            return InMemoryBank.UploadFails(r);
        });
        await Service().TransmitAsync(run.Id);
        At(2026, 5, 7);

        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3").RedateAsync(run.Id, "late"));

        var record = (await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile!.FileReference))!;
        Assert.Equal(PaymentFileTransmissionStatus.Failed, record.Status);
        Assert.Contains("Present", record.Attempts.Last(a => a.Action == PaymentFileTransmissionAction.Reconcile).Detail);
        Assert.Empty((await _h.Runs.GetByIdAsync(run.Id))!.EftFileHistory);
    }

    [Fact]
    public async Task Re_dating_a_failed_file_records_the_drop_check_and_refuses_when_the_drop_cannot_be_checked()
    {
        At(2026, 5, 4);
        var run = await RealRunAsync();
        _bank.Script.Enqueue(InMemoryBank.UploadFails);
        await Service().TransmitAsync(run.Id);
        At(2026, 5, 7);

        var unreachable = new UnreachableProbe();
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3", probe: unreachable).RedateAsync(run.Id, "late"));
        Assert.Empty((await _h.Runs.GetByIdAsync(run.Id))!.EftFileHistory);

        await Service("treasury-3").RedateAsync(run.Id, "late");
        var old = (await _store.GetAsync(FfsRunHarness.Tenant, run.EftFile!.FileReference))!;
        Assert.Equal(PaymentFileTransmissionStatus.Superseded, old.Status);
        Assert.Contains(old.Attempts, a => a.Action == PaymentFileTransmissionAction.Reconcile && a.Detail!.Contains("Absent"));
    }

    private sealed class UnreachableProbe : INachaRemoteFileProbe
    {
        public Task<NachaRemoteFileCheck> CheckAsync(string tenantId, string fileName, long expectedByteSize, CancellationToken cancellationToken = default)
            => throw new NachaTransmissionException("The bank's SFTP drop could not be checked (SocketException).");
    }

    /// <summary>Runs <paramref name="before"/> right before the first re-pin it forwards.</summary>
    private sealed class InterleavedEftFiles : IFfsEftFileService
    {
        private readonly IFfsEftFileService _inner;
        private Func<Task>? _before;

        public InterleavedEftFiles(IFfsEftFileService inner, Func<Task> before) { _inner = inner; _before = before; }

        public Task<FfsEftFileOutcome> GenerateAsync(string paymentRunId, string actorUserId, CancellationToken cancellationToken = default)
            => _inner.GenerateAsync(paymentRunId, actorUserId, cancellationToken);

        public async Task<FfsEftFileOutcome> RepinAsync(string paymentRunId, string actorUserId, string reason,
            string expectedFileReference, string expectedSha256, CancellationToken cancellationToken = default)
        {
            if (_before is { } before)
            {
                _before = null;
                await before();
            }
            return await _inner.RepinAsync(paymentRunId, actorUserId, reason, expectedFileReference, expectedSha256, cancellationToken);
        }
    }

    [Fact]
    public async Task A_requested_weekend_date_is_rolled_and_a_past_one_refused_at_create()
    {
        At(2026, 5, 4);

        var run = await RealRunAsync(requested: new DateTime(2026, 5, 16)); // a Saturday

        Assert.True(run.PaymentDateRequested);
        Assert.Equal(new DateTime(2026, 5, 18), run.EftFile!.EffectiveEntryDate.Date);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _h.Service().CreatePaymentRunAsync(new PaymentRunCriteria(), "maker-1", new DateTime(2026, 5, 1)));
    }

    [Fact]
    public async Task A_late_first_send_is_refused_then_re_dated_and_sent_once()
    {
        At(2026, 5, 4);
        var run = await RealRunAsync();
        var original = run.EftFile!;
        Assert.Equal(new DateTime(2026, 5, 5), original.EffectiveEntryDate.Date);

        At(2026, 5, 7); // nobody approved it in time
        await Assert.ThrowsAsync<PaymentFileApprovalStaleException>(() => Service().TransmitAsync(run.Id));
        Assert.Empty(_bank.Sent);

        // The run's maker and executor may not re-date it; a reason is required.
        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("maker-1").RedateAsync(run.Id, "late"));
        await Assert.ThrowsAsync<SeparationOfDutiesException>(() => Service("approver-1").RedateAsync(run.Id, "late"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().RedateAsync(run.Id, " "));

        var redated = await Service("treasury-2").RedateAsync(run.Id, "approval came after the effective date");

        Assert.Equal(($"{original.FileReference}-R1", 1), (redated.FileReference, redated.Revision));
        Assert.EndsWith("-R1.ach", redated.FileName);
        Assert.Equal(new DateTime(2026, 5, 8), redated.EffectiveEntryDate.Date);
        Assert.NotEqual(original.Sha256, redated.Sha256);
        var stored = (await _h.Runs.GetByIdAsync(run.Id))!;
        var history = Assert.Single(stored.EftFileHistory);
        Assert.Equal((original.Sha256, "treasury-2", redated.FileReference), (history.Sha256, history.SupersededBy, history.SupersededByFileReference));

        // The old file's record is Superseded (created, since it was never approved) and linked.
        var old = (await _store.GetAsync(FfsRunHarness.Tenant, original.FileReference))!;
        Assert.Equal((PaymentFileTransmissionStatus.Superseded, redated.FileReference), (old.Status, old.SupersededByFileReference));
        Assert.Equal(PaymentFileTransmissionAction.Superseded, old.Attempts.Last().Action);

        // The new file needs (and gets) a fresh approval; it is sent once.
        var sent = await Service().TransmitAsync(run.Id);
        Assert.Equal((PaymentFileTransmissionStatus.Transmitted, redated.FileReference, redated.Sha256), (sent.Status, sent.FileReference, sent.ApprovedSha256));
        Assert.EndsWith("-R1.ach", Assert.Single(_bank.Sent).FileName);
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service("treasury-3").TransmitAsync(run.Id)).Status);
        Assert.Single(_bank.Sent);
        Assert.Equal(PaymentFileTransmissionStatus.Superseded, (await _store.GetAsync(FfsRunHarness.Tenant, original.FileReference))!.Status);
    }

    [Fact]
    public async Task A_failed_file_past_its_date_can_be_re_dated_and_its_record_is_kept_superseded()
    {
        At(2026, 5, 4);
        var run = await RealRunAsync();
        _bank.Script.Enqueue(InMemoryBank.UploadFails);
        await Service().TransmitAsync(run.Id);
        var reference = run.EftFile!.FileReference;

        At(2026, 5, 6);
        await Service("treasury-3").RedateAsync(run.Id, "bank outage past the effective date");

        var old = (await _store.GetAsync(FfsRunHarness.Tenant, reference))!;
        Assert.Equal(PaymentFileTransmissionStatus.Superseded, old.Status);
        Assert.Contains(old.Attempts, a => a.Action == PaymentFileTransmissionAction.Transmit && a.Result == PaymentFileTransmissionStatus.Failed);
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service().TransmitAsync(run.Id)).Status);
        Assert.Equal(2, _bank.Sent.Count); // the failed upload and the re-dated send
        Assert.Single(_bank.Drop);
    }

    [Fact]
    public async Task Re_dating_is_refused_for_a_transmitted_file()
    {
        At(2026, 5, 4);
        var run = await RealRunAsync();
        await Service().TransmitAsync(run.Id);
        At(2026, 5, 8);

        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3").RedateAsync(run.Id, "again"));

        Assert.Empty((await _h.Runs.GetByIdAsync(run.Id))!.EftFileHistory);
        Assert.Single(_bank.Sent);
    }

    [Fact]
    public async Task Re_dating_is_refused_while_NeedsReview_and_allowed_once_the_bank_says_not_received()
    {
        At(2026, 5, 4);
        var run = await RealRunAsync();
        _bank.Script.Enqueue(InMemoryBank.AmbiguousNothingLanded);
        await Service().TransmitAsync(run.Id);
        At(2026, 5, 8);

        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3").RedateAsync(run.Id, "late"));
        Assert.Empty((await _h.Runs.GetByIdAsync(run.Id))!.EftFileHistory);

        await Service("treasury-4").ResolveAsync(run.Id, false, "bank confirmed no file received, ref 12");
        var redated = await Service("treasury-3").RedateAsync(run.Id, "not received; re-date");

        Assert.Equal(1, redated.Revision);
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service().TransmitAsync(run.Id)).Status);
        Assert.Equal(2, _bank.Sent.Count);
    }

    [Fact]
    public async Task A_file_that_can_still_be_sent_is_not_re_dated()
    {
        At(2026, 5, 4);
        var run = await RealRunAsync();

        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service().RedateAsync(run.Id, "just because"));

        Assert.Empty((await _h.Runs.GetByIdAsync(run.Id))!.EftFileHistory);
        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task A_superseded_record_is_never_sendable()
    {
        var run = await PinnedRunAsync();
        var superseded = Record(run, PaymentFileTransmissionStatus.Superseded);
        superseded.SupersededByFileReference = run.EftFile!.FileReference + "-R1";
        _store.Put(superseded);

        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service().TransmitAsync(run.Id));
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3").ReconcileAsync(run.Id));
        await Assert.ThrowsAsync<PaymentFileTransmissionStateException>(() => Service("treasury-3").ResolveAsync(run.Id, true, "x"));

        Assert.Empty(_bank.Sent);
    }

    [Fact]
    public async Task An_interrupted_re_date_can_be_completed()
    {
        At(2026, 5, 4);
        var run = await RealRunAsync();
        At(2026, 5, 7);
        // The record was superseded, but the new file was never pinned (a crash in between).
        var superseded = Record(run, PaymentFileTransmissionStatus.Superseded);
        superseded.SupersededByFileReference = run.EftFile!.FileReference + "-R1";
        _store.Put(superseded);

        var redated = await Service().RedateAsync(run.Id, "finish the re-date");

        Assert.Equal(1, redated.Revision);
        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, (await Service("treasury-3").TransmitAsync(run.Id)).Status);
    }

    [Fact]
    public async Task A_payment_reversed_after_approval_blocks_the_retry()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(InMemoryBank.UploadFails);
        await Service().TransmitAsync(run.Id);
        await _h.Reservations.TryReserveAsync(new global::PaymentService.Repositories.ClaimReservation
        {
            TenantId = FfsRunHarness.Tenant, Kind = global::PaymentService.Repositories.ClaimReservationKind.Reversal,
            ClaimId = "c1", RunId = "rr-1", RunNumber = "RR-1",
        });

        var ex = await Assert.ThrowsAsync<PaymentFileApprovalStaleException>(() => Service("treasury-3").TransmitAsync(run.Id));

        Assert.Contains("reversal run RR-1", ex.Message);
        Assert.Single(_bank.Sent);
    }

    [Fact]
    public async Task A_payment_reissued_by_another_run_blocks_the_send()
    {
        var run = await PinnedRunAsync();
        var held = (await _h.Reservations.GetAsync(global::PaymentService.Repositories.ClaimReservationKind.Payment, FfsRunHarness.Tenant, "c2"))!;
        Assert.True(await _h.Reservations.TryDeleteIfUnchangedAsync(held));
        await _h.Reservations.TryReserveAsync(new global::PaymentService.Repositories.ClaimReservation
        {
            TenantId = FfsRunHarness.Tenant, Kind = global::PaymentService.Repositories.ClaimReservationKind.Payment,
            ClaimId = "c2", RunId = "run-other", RunNumber = "PR-OTHER",
        });

        var ex = await Assert.ThrowsAsync<PaymentFileApprovalStaleException>(() => Service().TransmitAsync(run.Id));

        Assert.Contains("PR-OTHER", ex.Message);
        Assert.Empty(_bank.Sent);
    }

    [Theory]
    [InlineData("CHK")]
    [InlineData("exception")]
    [InlineData("amount")]
    public async Task A_payment_changed_after_approval_blocks_the_send(string change)
    {
        var run = await PinnedRunAsync();
        var payment = _h.Payments.All.First(p => p.RunId == run.Id);
        switch (change)
        {
            case "CHK": payment.PaymentMethod = "CHK"; break;
            case "exception": payment.Status = PaymentStatus.Exception; break;
            default: payment.TotalPaymentAmount += 1m; break;
        }

        await Assert.ThrowsAsync<PaymentFileApprovalStaleException>(() => Service().TransmitAsync(run.Id));

        Assert.Empty(_bank.Sent);
    }

    [Fact]
    public async Task Resolve_still_works_while_transmission_is_disabled()
    {
        var run = await PinnedRunAsync();
        _bank.Script.Enqueue(InMemoryBank.AmbiguousNothingLanded);
        await Service().TransmitAsync(run.Id);
        _options.Enabled = false;

        var resolved = await Service("treasury-3").ResolveAsync(run.Id, true, "bank confirmed receipt, ref 77");

        Assert.Equal(PaymentFileTransmissionStatus.Transmitted, resolved.Status);
        await Assert.ThrowsAsync<BankTransmissionDisabledException>(() => Service("treasury-3").TransmitAsync(run.Id));
        Assert.Single(_bank.Sent);
    }

    [Theory]
    [InlineData("2026-01-01", false)] // New Year's Day
    [InlineData("2026-01-19", false)] // Martin Luther King Jr. Day
    [InlineData("2026-05-25", false)] // Memorial Day
    [InlineData("2026-07-03", true)]  // July 4 is a Saturday: not moved, the Friday is open
    [InlineData("2027-07-05", false)] // July 4 is a Sunday: observed Monday
    [InlineData("2026-11-26", false)] // Thanksgiving
    [InlineData("2026-11-27", true)]
    [InlineData("2026-05-09", false)] // Saturday
    [InlineData("2026-05-05", true)]
    public void Banking_days_follow_the_federal_reserve_calendar(string date, bool banking)
        => Assert.Equal(banking, AchBankingCalendar.IsBankingDay(DateTime.Parse(date)));

    private void AssertNoBankNumbers()
    {
        foreach (var text in new[] { _log.All, _store.RawJson })
        {
            Assert.DoesNotContain(Account, text);
            Assert.DoesNotContain(TinDigits, text);
        }
    }

    private PaymentFileTransmission Record(PaymentRun run, PaymentFileTransmissionStatus status, DateTime? leaseUntil = null) => new()
    {
        TenantId = run.TenantId,
        PaymentRunId = run.Id,
        PaymentRunNumber = run.PaymentRunNumber,
        FileReference = run.EftFile!.FileReference,
        FileName = run.EftFile.FileName,
        ApprovedSha256 = run.EftFile.Sha256,
        ByteSize = run.EftFile.ByteSize,
        EntryCount = run.EftFile.EntryCount,
        TotalCreditAmount = run.EftFile.TotalCreditAmount,
        ApprovedBy = "treasury-1",
        ApprovedPaymentIds = run.EftFile.Entries.SelectMany(e => e.PaymentIds).OrderBy(i => i, StringComparer.Ordinal).ToList(),
        Status = status,
        AttemptCount = status == PaymentFileTransmissionStatus.Transmitting ? 1 : 0,
        LeaseUntil = leaseUntil,
    };

    private sealed class SameStatus : IEqualityComparer<PaymentFileTransmission>
    {
        public bool Equals(PaymentFileTransmission? x, PaymentFileTransmission? y) => x?.Status == y?.Status && x?.Version == y?.Version;
        public int GetHashCode(PaymentFileTransmission obj) => obj.Status.GetHashCode();
    }
}

/// <summary>The bank's drop in memory, behind the transmitter interface; scripted failures.</summary>
internal sealed class InMemoryBank : INachaTransmitter, INachaRemoteFileProbe
{
    public ConcurrentQueue<Func<NachaTransmissionRequest, NachaTransmissionReceipt>> Script { get; } = new();
    public ConcurrentBag<NachaTransmissionRequest> Sent { get; } = new();
    public ConcurrentDictionary<string, byte[]> Drop { get; } = new();
    public Func<Task>? BeforeSend { get; set; }
    public Action? AfterSend { get; set; }
    public int Probes;

    public static NachaTransmissionReceipt UploadFails(NachaTransmissionRequest _)
        => throw new NachaTransmissionException("The upload to the bank's SFTP server failed (SshConnectionException).");

    /// <summary>The file is in place, but the client never learnt it.</summary>
    public NachaTransmissionReceipt RenameReplyLost(NachaTransmissionRequest request)
    {
        Drop[request.FileName] = NachaFileFacts.Encode(request.Content);
        throw new NachaTransmissionException("renaming it into place failed and the result could not be checked", deliveryUnknown: true);
    }

    public static NachaTransmissionReceipt AmbiguousNothingLanded(NachaTransmissionRequest _)
        => throw new NachaTransmissionException("renaming it into place failed and the result could not be checked", deliveryUnknown: true);

    private NachaTransmissionReceipt Deliver(NachaTransmissionRequest request)
    {
        var bytes = NachaFileFacts.Encode(request.Content);
        if (!Drop.TryAdd(request.FileName, bytes))
            throw new NachaTransmissionException("already in the drop", deliveryUnknown: true);
        var facts = NachaFileFacts.From(bytes);
        return new NachaTransmissionReceipt
        {
            TenantId = request.TenantId,
            FileReference = request.FileReference,
            RemoteFileName = request.FileName,
            Destination = "sftp://bank.test:22/inbound",
            ByteSize = facts.ByteSize,
            Sha256 = facts.Sha256,
            EntryCount = facts.EntryCount,
            TotalCreditAmount = facts.TotalCreditAmount,
            TotalDebitAmount = facts.TotalDebitAmount,
            TransmittedAt = DateTime.UtcNow,
            TransmittedBy = request.TransmittedBy,
        };
    }

    public async Task<NachaTransmissionReceipt> TransmitAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default)
    {
        Sent.Add(request);
        if (BeforeSend is { } before)
            await before();
        try
        {
            return Script.TryDequeue(out var step) ? step(request) : Deliver(request);
        }
        finally
        {
            AfterSend?.Invoke();
        }
    }

    public Task<NachaRemoteFileCheck> CheckAsync(string tenantId, string fileName, long expectedByteSize, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Probes);
        var presence = !Drop.TryGetValue(fileName, out var bytes) ? NachaRemoteFilePresence.Absent
            : bytes.LongLength == expectedByteSize ? NachaRemoteFilePresence.Present
            : NachaRemoteFilePresence.DifferentSize;
        return Task.FromResult(new NachaRemoteFileCheck { Presence = presence, Destination = "sftp://bank.test:22/inbound", RemoteByteSize = bytes?.LongLength });
    }
}

/// <summary>
/// An SFTP server in memory behind <see cref="ISftpSessionFactory"/>, for the
/// real <see cref="SftpNachaTransmitter"/>. Before any session it runs the real
/// <see cref="PinnedHostKeyCheck"/> on the key it presents, exactly as
/// <see cref="SshNetSftpSessionFactory"/> does on the wire.
/// </summary>
internal sealed class InMemorySftp : ISftpSessionFactory
{
    private readonly byte _presentedSeed;

    /// <param name="presentedSeed">Which host key this server presents (see <see cref="Key"/>).</param>
    public InMemorySftp(byte presentedSeed) => _presentedSeed = presentedSeed;

    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public int Sessions;
    public int Uploads;
    public bool RenameReplyLostAndConnectionDies { get; set; }
    private bool _dead;

    public static HostKeyEventArgs Key(byte seed)
    {
        var data = new byte[64];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(seed + i);
        return new HostKeyEventArgs(new KeyHostAlgorithm("ssh-ed25519", new ED25519Key(data)));
    }

    public static SftpNachaTransmitter Transmitter(InMemorySftp sftp, string pin)
        => new(new Settings(pin), new Secrets(), sftp, NullLogger<SftpNachaTransmitter>.Instance);

    public ISftpSession Connect(SftpConnectParameters parameters)
    {
        var check = new PinnedHostKeyCheck(parameters.HostKeyFingerprint);
        var presented = Key(_presentedSeed);
        check.OnHostKeyReceived(this, presented);
        if (!presented.CanTrust || !check.Trusted)
            throw check.RejectionException();
        _dead = false;
        Interlocked.Increment(ref Sessions);
        return new Session(this);
    }

    private sealed class Session : ISftpSession
    {
        private readonly InMemorySftp _s;
        public Session(InMemorySftp s) => _s = s;
        private void Alive() { if (_s._dead) throw new IOException("connection lost"); }
        public bool Exists(string path) { Alive(); return _s.Files.ContainsKey(path); }
        public long? Size(string path) { Alive(); return _s.Files[path].LongLength; }
        public void Upload(Stream content, string path)
        {
            Alive();
            Interlocked.Increment(ref _s.Uploads);
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            _s.Files[path] = ms.ToArray();
        }
        public void Rename(string from, string to)
        {
            Alive();
            _s.Files[to] = _s.Files[from];
            _s.Files.TryRemove(from, out _);
            if (_s.RenameReplyLostAndConnectionDies)
            {
                _s._dead = true;
                throw new IOException("connection reset before the rename reply");
            }
        }
        public void Delete(string path) { Alive(); _s.Files.TryRemove(path, out _); }
        public void Dispose() { }
    }

    private sealed class Settings : INachaTransmissionSettingsSource
    {
        private readonly string _pin;
        public Settings(string pin) => _pin = pin;
        public Task<NachaTransmissionSettings?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult<NachaTransmissionSettings?>(new NachaTransmissionSettings
            {
                Enabled = true,
                Host = "sftp.bank.example",
                Port = 22,
                Username = "cho-plan",
                PrivateKeySecretRef = $"nacha--{tenantId}--key",
                HostKeyFingerprint = _pin,
                RemoteDirectory = "/inbound/ach",
            });
    }

    private sealed class Secrets : INachaSecretReader
    {
        public Task<string> GetSecretAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult("-----BEGIN OPENSSH PRIVATE KEY-----test-----END OPENSSH PRIVATE KEY-----");
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
    public string All => string.Join("\n", Entries.Select(e => e.Message));
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Enqueue((logLevel, formatter(state, exception)));
}

internal sealed class MutableClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 5, 1, 15, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
