using ArService.Gl;
using CloudHealthOffice.Finance.Contracts;

namespace ArService.Tests.Gl;

/// <summary>
/// Payments voided before transmission, the GL cutover, reposting after a reversal, the period
/// close serialized with posts (and a closed extract that never changes), and reconciliation by
/// the file's own payment ids.
/// </summary>
public class GlReviewFixesTests
{
    private readonly InMemoryGl _gl = new();
    private readonly GlClock _clock = new();
    private GlPostingOptions _options = GlFixtures.Options();

    public GlReviewFixesTests() => GlFixtures.SeedChart(_gl);

    private GlPostingService Service() => GlFixtures.Service(_gl, _options, _clock);

    private decimal Balance(GlPostingRole role) => _gl.Entries.SelectMany(e => e.Lines).Where(l => l.Role == role).Sum(l => l.Debit - l.Credit);

    private static List<GlPaymentLine> AchPayments(string runId, params decimal[] amounts)
        => amounts.Select((a, i) => new GlPaymentLine
        {
            PaymentId = $"{runId}-p{i + 1}", CheckNumber = $"000100000{i + 1}", PayeeNpi = $"{i + 1}{i + 1}{i + 1}", PaymentMethod = "ACH", NetAmount = a,
        }).ToList();

    // ── 1. A payment reversed before its file was sent ─────────────────────────

    [Fact]
    public async Task A_payment_voided_before_transmission_relieves_its_payable_against_the_receivable_and_the_run_balances()
    {
        // Run: two ACH payments, 100 and 40. The 40 is reversed (recouped) before the file
        // goes; the re-pinned file pays only the 100 and the 40 is voided.
        await Service().IngestAsync(GlFixtures.RunExecuted("run-1", null, AchPayments("run-1", 100m, 40m)), GlFixtures.Tenant);
        await Service().IngestAsync(GlFixtures.ReversalExecuted("rr-1", amount: 40m), GlFixtures.Tenant);
        var voided = await Service().IngestAsync(GlFixtures.PaymentVoided("run-1", "run-1-p2", 40m), GlFixtures.Tenant);
        await Service().IngestAsync(GlFixtures.FileTransmitted("run-1", "FFS-PR-run-1-R1", 100m, paymentIds: ["run-1-p1"]), GlFixtures.Tenant);

        voided.Status.Should().Be(GlSourceEventStatus.Posted);
        var entry = _gl.Entries.Single(e => e.Kind == GlEntryKind.PaymentVoid);
        entry.Id.Should().Be("tenant-1:void:run-1-p2");
        entry.Lines.Select(l => (l.Role, l.Debit, l.Credit)).Should().Equal(
            (GlPostingRole.ClaimsPayable, 40m, 0m), (GlPostingRole.ProviderReceivable, 0m, 40m));
        // Expense: 140 accrued, 40 recouped once (never twice). No receivable is left for an unpaid payment.
        Balance(GlPostingRole.ClaimsExpense).Should().Be(100m);
        Balance(GlPostingRole.ProviderReceivable).Should().Be(0m);
        Balance(GlPostingRole.ClaimsPayable).Should().Be(0m);
        Balance(GlPostingRole.AchInTransit).Should().Be(-100m);

        var report = GlReconciliationBuilder.Build(GlFixtures.Tenant, null, _gl.Events, _gl.Entries);
        var run = report.Runs.Single();
        (run.AccruedPayable, run.TransmittedPayable, run.VoidedPayable, run.OutstandingPayable, run.RunAchTotal, run.RunVoidedTotal, run.RunCheckTotal)
            .Should().Be((140m, 100m, 40m, 0m, 100m, 40m, 0m));
        run.Flags.Should().BeEmpty();
        report.Balanced.Should().BeTrue();

        // Redelivered: still one void entry.
        (await Service().IngestAsync(GlFixtures.PaymentVoided("run-1", "run-1-p2", 40m), GlFixtures.Tenant)).Duplicate.Should().BeTrue();
        _gl.Entries.Count(e => e.Kind == GlEntryKind.PaymentVoid).Should().Be(1);
    }

    [Fact]
    public async Task A_parked_void_is_a_reconciliation_difference()
    {
        _options = GlFixtures.Options(unmapped: GlPostingRole.ProviderReceivable);
        await Service().IngestAsync(GlFixtures.RunExecuted("run-1", null, AchPayments("run-1", 100m)), GlFixtures.Tenant);

        var parked = await Service().IngestAsync(GlFixtures.PaymentVoided("run-1", "run-1-p1", 100m), GlFixtures.Tenant);

        parked.ParkReason.Should().Be(GlParkReason.NeedsMapping);
        GlReconciliationBuilder.Build(GlFixtures.Tenant, null, _gl.Events, _gl.Entries).Runs.Single().Flags.Should().Contain(GlReconciliationFlag.VoidParked);
    }

    // ── 2. Cutover ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Events_of_runs_executed_before_the_cutover_are_registered_but_parked_PreCutover()
    {
        var before = new DateTime(2026, 4, 28, 14, 0, 0, DateTimeKind.Utc);
        var accrual = await Service().IngestAsync(GlFixtures.RunExecuted("old-1", before), GlFixtures.Tenant);
        // The file of that run, sent after the cutover: the event says when the run was executed.
        var file = await Service().IngestAsync(GlFixtures.FileTransmitted("old-1", "FFS-PR-old-1", 100m, runExecutedAt: before), GlFixtures.Tenant);
        // An older event without the date: the run's accrual (PreCutover) decides.
        var undated = await Service().IngestAsync(GlFixtures.FileTransmitted("old-1", "FFS-PR-old-1-R1", 100m), GlFixtures.Tenant);
        var reversal = await Service().IngestAsync(GlFixtures.ReversalExecuted("rr-old") with
        {
            PayloadJson = GlFixtures.ReversalExecuted("rr-old").PayloadJson.Replace("2026-05-22T10:00:00Z", "2026-04-29T10:00:00Z"),
        }, GlFixtures.Tenant);

        new[] { accrual, file, undated, reversal }.Should().OnlyContain(r => r.Status == GlSourceEventStatus.Parked && r.ParkReason == GlParkReason.PreCutover);
        accrual.ParkDetail.Should().Contain("before the GL cutover 2026-05-01");
        _gl.Entries.Should().BeEmpty();
        _gl.Events.Should().HaveCount(4); // visible, not dropped

        // A run executed on the cutover date posts.
        (await Service().IngestAsync(GlFixtures.RunExecuted("new-1", GlFixtures.Cutover), GlFixtures.Tenant)).Status.Should().Be(GlSourceEventStatus.Posted);

        var report = GlReconciliationBuilder.Build(GlFixtures.Tenant, null, _gl.Events, _gl.Entries);
        report.Runs.Single(r => r.PaymentRunId == "old-1").Flags.Should().Equal(GlReconciliationFlag.PreCutover);
        report.Difference.Should().Be(0m);
    }

    [Fact]
    public async Task A_file_whose_run_is_not_known_yet_waits_for_the_accrual_then_posts_on_retry()
    {
        var file = await Service().IngestAsync(GlFixtures.FileTransmitted("run-1", "FFS-PR-run-1", 100m), GlFixtures.Tenant);

        file.ParkReason.Should().Be(GlParkReason.PreCutover);
        file.ParkDetail.Should().Contain("not known yet");

        await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        (await Service().RetryAsync(GlFixtures.Tenant, file.EventId, null, null, "controller-1")).Status.Should().Be(GlSourceEventStatus.Posted);
    }

    [Fact]
    public async Task A_PreCutover_event_posts_only_when_finance_says_so_with_a_reason()
    {
        var parked = await Service().IngestAsync(GlFixtures.RunExecuted("old-1", new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc)), GlFixtures.Tenant);

        (await Service().RetryAsync(GlFixtures.Tenant, parked.EventId, null, null, "controller-1")).ParkReason.Should().Be(GlParkReason.PreCutover);
        await FluentActions.Awaiting(() => Service().RetryAsync(GlFixtures.Tenant, parked.EventId, null, " ", "controller-1", includeBeforeCutover: true))
            .Should().ThrowAsync<ArgumentException>();

        var posted = await Service().RetryAsync(GlFixtures.Tenant, parked.EventId, null, "left out of the opening balance", "controller-1", includeBeforeCutover: true);

        posted.Status.Should().Be(GlSourceEventStatus.Posted);
        _gl.Entries.Single().Reason.Should().Contain("before the GL cutover").And.Contain("left out of the opening balance");
    }

    [Fact]
    public async Task A_void_follows_its_recoupment_across_the_cutover_not_its_payment_run()
    {
        var before = new DateTime(2026, 4, 28, 14, 0, 0, DateTimeKind.Utc);
        // The payment run is before the cutover (in the opening balances); its file was never sent.
        (await Service().IngestAsync(GlFixtures.RunExecuted("old-1", before, AchPayments("old-1", 40m)), GlFixtures.Tenant)).ParkReason
            .Should().Be(GlParkReason.PreCutover);
        // The reversal run is after it: its recoupment posts here, so the void must post too.
        (await Service().IngestAsync(GlFixtures.ReversalExecuted("rr-1", amount: 40m), GlFixtures.Tenant)).Status.Should().Be(GlSourceEventStatus.Posted);

        var voided = await Service().IngestAsync(GlFixtures.PaymentVoided("old-1", "old-1-p1", 40m, runExecutedAt: before,
            reversalExecutedAt: new DateTime(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc)), GlFixtures.Tenant);

        voided.Status.Should().Be(GlSourceEventStatus.Posted);
        Balance(GlPostingRole.ProviderReceivable).Should().Be(0m); // recoupment receivable cleared by the void
    }

    [Fact]
    public async Task A_void_whose_recoupment_is_before_the_cutover_stays_out_of_this_GL()
    {
        var before = new DateTime(2026, 4, 28, 14, 0, 0, DateTimeKind.Utc);
        var oldReversal = GlFixtures.ReversalExecuted("rr-old");
        await Service().IngestAsync(oldReversal with { PayloadJson = oldReversal.PayloadJson.Replace("2026-05-22T10:00:00Z", "2026-04-29T10:00:00Z") },
            GlFixtures.Tenant);

        var voided = await Service().IngestAsync(GlFixtures.PaymentVoided("old-1", "old-1-p1", 40m, runExecutedAt: before, reversalRunId: "rr-old",
            reversalExecutedAt: new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc)), GlFixtures.Tenant);

        voided.ParkReason.Should().Be(GlParkReason.PreCutover);
        voided.ParkDetail.Should().Contain("recoupment");
    }

    // ── 3. Repost after reversal ───────────────────────────────────────────────

    [Fact]
    public async Task A_parked_second_file_is_reposted_under_a_new_key_once_the_first_entry_is_reversed()
    {
        await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        var first = await Service().IngestAsync(GlFixtures.FileTransmitted(fileReference: "FFS-PR-run-1", amount: 90m), GlFixtures.Tenant);
        var second = await Service().IngestAsync(GlFixtures.FileTransmitted(fileReference: "FFS-PR-run-1-R1", amount: 100m), GlFixtures.Tenant);
        second.ParkReason.Should().Be(GlParkReason.DuplicateBusinessKey);

        // Refused while the first entry stands, and without a reason.
        await FluentActions.Awaiting(() => Service().RepostAsync(GlFixtures.Tenant, second.EventId, "the R1 file is the one at the bank", null, "controller-1"))
            .Should().ThrowAsync<GlStateException>();
        await FluentActions.Awaiting(() => Service().RepostAsync(GlFixtures.Tenant, second.EventId, " ", null, "controller-1"))
            .Should().ThrowAsync<ArgumentException>();

        await Service().ReverseAsync(GlFixtures.Tenant, first.EntryId!, "posted from the wrong file", null, "controller-1");
        // Second person: whoever reversed the entry may not repost its replacement.
        await FluentActions.Awaiting(() => Service().RepostAsync(GlFixtures.Tenant, second.EventId, "the R1 file is the one at the bank", null, "controller-1"))
            .Should().ThrowAsync<GlForbiddenException>();
        var reposted = await Service().RepostAsync(GlFixtures.Tenant, second.EventId, "the R1 file is the one at the bank", null, "controller-2");

        reposted.Status.Should().Be(GlSourceEventStatus.Posted);
        reposted.EntryId.Should().Be("tenant-1:ach:run-1:r1");
        var entry = _gl.Entries.Single(e => e.Id == reposted.EntryId);
        entry.RepostOf.Should().Be("tenant-1:ach:run-1");
        entry.PostedBy.Should().Be("controller-2");
        entry.Reason.Should().Contain("repost of tenant-1:ach:run-1");
        _gl.Events.Single(e => e.EventId == second.EventId).EntryId.Should().Be(reposted.EntryId);

        // The latest entry stands again: no further repost until it is reversed too.
        await FluentActions.Awaiting(() => Service().RepostAsync(GlFixtures.Tenant, first.EventId, "back to R0", null, "controller-1"))
            .Should().ThrowAsync<GlStateException>();
        await Service().ReverseAsync(GlFixtures.Tenant, reposted.EntryId!, "wrong again", null, "controller-1");
        (await Service().RepostAsync(GlFixtures.Tenant, first.EventId, "R0 after all", null, "controller-2")).EntryId.Should().Be("tenant-1:ach:run-1:r2");

        // Chain-aware: one live ACH entry for the run, net 90 of payable relieved.
        var run = GlReconciliationBuilder.Build(GlFixtures.Tenant, null, _gl.Events, _gl.Entries).Runs.Single();
        run.TransmittedPayable.Should().Be(90m);
        run.Flags.Should().NotContain(GlReconciliationFlag.EntryReversed);
    }

    [Fact]
    public async Task A_repost_that_cannot_post_changes_nothing()
    {
        await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        var accrual = _gl.Entries.Single();
        await Service().ReverseAsync(GlFixtures.Tenant, accrual.Id, "wrong", null, "controller-1");
        _options = GlFixtures.Options(unmapped: GlPostingRole.ClaimsExpense);
        var before = _gl.Events.Single();

        await FluentActions.Awaiting(() => Service().RepostAsync(GlFixtures.Tenant, before.EventId, "again", null, "controller-2"))
            .Should().ThrowAsync<GlStateException>().WithMessage("*NeedsMapping*");

        _gl.Events.Single().Should().BeEquivalentTo(before);
        _gl.Entries.Should().HaveCount(2);
        GlReconciliationBuilder.Build(GlFixtures.Tenant, null, _gl.Events, _gl.Entries).Runs.Single().Flags.Should().Contain(GlReconciliationFlag.EntryReversed);
    }

    // ── 4. Period close vs posts ───────────────────────────────────────────────

    [Fact]
    public async Task A_close_waits_for_a_post_in_flight_and_nothing_lands_after_it()
    {
        _clock.Now = new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero);
        var inFlight = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _gl.BeforeInsert = async _ =>
        {
            _gl.BeforeInsert = null;
            inFlight.SetResult();
            await release.Task;
        };

        var post = Task.Run(() => Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant));
        await inFlight.Task;

        // The post holds the period: the close is refused and nothing changed.
        await FluentActions.Awaiting(() => Service().ClosePeriodAsync(GlFixtures.Tenant, "2026-05", "May", "controller-1"))
            .Should().ThrowAsync<GlStateException>().WithMessage("*being posted*");
        release.SetResult();
        (await post).Status.Should().Be(GlSourceEventStatus.Posted);
        _gl.LiveLeases("2026-05").Should().Be(0);

        var closed = await Service().ClosePeriodAsync(GlFixtures.Tenant, "2026-05", "May", "controller-1");
        var extract = await Service().GetExtractAsync(GlFixtures.Tenant, "2026-05");
        closed.ExtractSha256.Should().Be(extract.Control.Sha256);
        closed.ExtractEntryCount.Should().Be(1);

        // After the close: parked, never written; the closed extract and its SHA do not change.
        var late = await Service().IngestAsync(GlFixtures.RunExecuted("run-2"), GlFixtures.Tenant);
        late.ParkReason.Should().Be(GlParkReason.ClosedPeriod);
        await FluentActions.Awaiting(() => Service().ReverseAsync(GlFixtures.Tenant, "tenant-1:accrual:run-1", "x", new DateTime(2026, 5, 31), "controller-1"))
            .Should().ThrowAsync<GlStateException>();
        (await Service().GetExtractAsync(GlFixtures.Tenant, "2026-05")).Csv.Should().Be(extract.Csv);
    }

    [Fact]
    public async Task A_post_that_outlives_its_lease_window_writes_nothing()
    {
        // The clock jumps past the window right after the lease is granted (a stalled post).
        var service = Service();
        var slow = new GlPostingService(_gl, _gl, new LeaseObservingPeriods(_gl, () => _clock.Now = _clock.Now.Add(GlPostingService.MaxPostDuration).AddSeconds(1)), _gl,
            Microsoft.Extensions.Options.Options.Create(_options), Microsoft.Extensions.Logging.Abstractions.NullLogger<GlPostingService>.Instance, _clock);

        await FluentActions.Awaiting(() => slow.IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant)).Should().ThrowAsync<GlStateException>().WithMessage("*took too long*");

        _gl.Entries.Should().BeEmpty();
        _gl.LiveLeases("2026-05").Should().Be(0);
        (await service.IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant)).Status.Should().Be(GlSourceEventStatus.Posted);
    }

    [Fact]
    public async Task A_closed_period_whose_journal_changed_refuses_its_extract()
    {
        await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        _clock.Now = new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero);
        await Service().ClosePeriodAsync(GlFixtures.Tenant, "2026-05", "May", "controller-1");
        var entry = _gl.Entries.Single();
        entry.Id = "tenant-1:accrual:smuggled";
        _gl.ForceInsert(entry);

        await FluentActions.Awaiting(() => Service().GetExtractAsync(GlFixtures.Tenant, "2026-05")).Should().ThrowAsync<GlIntegrityException>();
    }

    // ── 5. Reconciliation by the file's payment ids ─────────────────────────────

    [Fact]
    public async Task A_run_with_a_check_fallback_reconciles_by_the_files_payment_ids()
    {
        // Both payments were ACH at execution; the second payee had no approved account when the
        // file was built and was paid by check instead (a check fallback): the file pays 100 only.
        await Service().IngestAsync(GlFixtures.RunExecuted("run-1", null, AchPayments("run-1", 100m, 60m)), GlFixtures.Tenant);
        await Service().IngestAsync(GlFixtures.FileTransmitted("run-1", "FFS-PR-run-1", 100m, paymentIds: ["run-1-p1"]), GlFixtures.Tenant);

        var report = GlReconciliationBuilder.Build(GlFixtures.Tenant, null, _gl.Events, _gl.Entries);

        var run = report.Runs.Single();
        (run.RunAchTotal, run.RunCheckTotal, run.OutstandingPayable).Should().Be((100m, 60m, 60m));
        run.Flags.Should().Equal(GlReconciliationFlag.PayableOutstanding);
        report.Balanced.Should().BeTrue();
    }

    [Fact]
    public async Task A_file_naming_a_payment_the_run_did_not_accrue_is_a_difference()
    {
        await Service().IngestAsync(GlFixtures.RunExecuted("run-1", null, AchPayments("run-1", 100m)), GlFixtures.Tenant);
        await Service().IngestAsync(GlFixtures.FileTransmitted("run-1", "FFS-PR-run-1", 100m, paymentIds: ["run-1-p1", "run-7-p1"]), GlFixtures.Tenant);

        var report = GlReconciliationBuilder.Build(GlFixtures.Tenant, null, _gl.Events, _gl.Entries);

        report.Runs.Single().Flags.Should().Contain(GlReconciliationFlag.FileDiffersFromAchPayments);
        report.Balanced.Should().BeFalse();
    }

    [Fact]
    public async Task The_reconciliation_pages_its_runs_but_totals_the_whole_ledger()
    {
        for (var i = 1; i <= 5; i++)
            await Service().IngestAsync(GlFixtures.RunExecuted($"run-{i}", null, AchPayments($"run-{i}", 10m)), GlFixtures.Tenant);

        var page = GlReconciliationBuilder.Build(GlFixtures.Tenant, null, _gl.Events, _gl.Entries, offset: 2, limit: 2);

        page.Runs.Select(r => r.PaymentRunId).Should().Equal("run-3", "run-4");
        (page.TotalRuns, page.Offset, page.RunsOutstandingPayable, page.GlClaimsPayableBalance).Should().Be((5, 2, 50m, 50m));
    }

    /// <summary>A period store that advances the clock as soon as the lease is granted (a slow post).</summary>
    private sealed class LeaseObservingPeriods : IGlPeriodRepository
    {
        private readonly IGlPeriodRepository _inner;
        private readonly Action _onBegin;
        public LeaseObservingPeriods(IGlPeriodRepository inner, Action onBegin) { _inner = inner; _onBegin = onBegin; }
        public Task<GlPeriod?> GetAsync(string tenantId, string period, CancellationToken cancellationToken = default) => _inner.GetAsync(tenantId, period, cancellationToken);
        public async Task<bool> TryBeginPostAsync(string tenantId, string period, string token, DateTime expiresAt, CancellationToken cancellationToken = default)
        {
            var ok = await _inner.TryBeginPostAsync(tenantId, period, token, expiresAt, cancellationToken);
            _onBegin();
            return ok;
        }
        public Task EndPostAsync(string tenantId, string period, string token, CancellationToken cancellationToken = default) => _inner.EndPostAsync(tenantId, period, token, cancellationToken);
        public Task<GlPeriodCloseOutcome> TryCloseAsync(string tenantId, string period, DateTime now, string closedBy, string? reason, CancellationToken cancellationToken = default)
            => _inner.TryCloseAsync(tenantId, period, now, closedBy, reason, cancellationToken);
        public Task<bool> TrySetExtractControlAsync(string tenantId, string period, GlExtractControl control, CancellationToken cancellationToken = default)
            => _inner.TrySetExtractControlAsync(tenantId, period, control, cancellationToken);
        public Task<IReadOnlyList<GlPeriod>> ListAsync(string tenantId, CancellationToken cancellationToken = default) => _inner.ListAsync(tenantId, cancellationToken);
    }
}
