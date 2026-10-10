using ArService.Gl;
using ArService.Models;
using CloudHealthOffice.Finance.Contracts;

namespace ArService.Tests.Gl;

/// <summary>
/// Double-entry posting from GL source events: balanced entries, idempotency on the event
/// id and on the business key, recoupments and operator reversals, closed periods and
/// missing mappings parking (never dropping), invalid payloads, the off switch.
/// </summary>
public class GlPostingServiceTests
{
    private readonly InMemoryGl _gl = new();
    private readonly GlClock _clock = new();
    private GlPostingOptions _options = GlFixtures.Options();

    public GlPostingServiceTests() => GlFixtures.SeedChart(_gl);

    private GlPostingService Service() => GlFixtures.Service(_gl, _options, _clock);

    private static decimal Sum(GlJournalEntry e, GlPostingRole role, bool debit)
        => e.Lines.Where(l => l.Role == role).Sum(l => debit ? l.Debit : l.Credit);

    [Fact]
    public async Task A_payment_run_accrues_gross_expense_against_net_payable_and_recovered_receivables()
    {
        var result = await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);

        result.Status.Should().Be(GlSourceEventStatus.Posted);
        var entry = _gl.Entries.Should().ContainSingle().Subject;
        entry.Kind.Should().Be(GlEntryKind.ClaimsAccrual);
        entry.Id.Should().Be("tenant-1:accrual:run-1");
        Sum(entry, GlPostingRole.ClaimsExpense, debit: true).Should().Be(170m);    // 100 + 50 + 20 offset
        Sum(entry, GlPostingRole.ClaimsPayable, debit: false).Should().Be(150m);   // what the providers are paid
        Sum(entry, GlPostingRole.ProviderReceivable, debit: false).Should().Be(20m);
        entry.TotalDebit.Should().Be(entry.TotalCredit).And.Be(170m);
        entry.EntryDate.Should().Be(new DateTime(2026, 5, 20));
        entry.Period.Should().Be("2026-05");
        entry.Lines.Select(l => l.AccountNumber).Should().BeEquivalentTo(new[] { "5100", "2100", "1250" });
    }

    [Fact]
    public async Task Transmission_moves_the_payable_to_ach_in_transit()
    {
        await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);

        await Service().IngestAsync(GlFixtures.FileTransmitted(), GlFixtures.Tenant);

        var ach = _gl.Entries.Single(e => e.Kind == GlEntryKind.AchTransmission);
        ach.Id.Should().Be("tenant-1:ach:run-1");
        Sum(ach, GlPostingRole.ClaimsPayable, debit: true).Should().Be(100m);
        Sum(ach, GlPostingRole.AchInTransit, debit: false).Should().Be(100m);
    }

    [Fact]
    public async Task A_redelivered_event_posts_once()
    {
        var envelope = GlFixtures.RunExecuted();

        var first = await Service().IngestAsync(envelope, GlFixtures.Tenant);
        var second = await Service().IngestAsync(envelope, GlFixtures.Tenant);

        first.Duplicate.Should().BeFalse();
        second.Duplicate.Should().BeTrue();
        second.EntryId.Should().Be(first.EntryId);
        _gl.Entries.Should().ContainSingle();
        _gl.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task The_same_event_id_with_another_payload_is_refused_and_the_first_stands()
    {
        var envelope = GlFixtures.RunExecuted();
        await Service().IngestAsync(envelope, GlFixtures.Tenant);
        var forged = GlFixtures.RunExecuted(ach: 900m) with { EventId = envelope.EventId };

        await FluentActions.Awaiting(() => Service().IngestAsync(forged, GlFixtures.Tenant)).Should().ThrowAsync<GlEventConflictException>();

        Sum(_gl.Entries.Single(), GlPostingRole.ClaimsPayable, debit: false).Should().Be(150m);
    }

    [Fact]
    public async Task A_second_event_about_the_same_run_never_posts_twice()
    {
        await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        var again = GlFixtures.RunExecuted() with { EventId = Guid.NewGuid().ToString() };

        var result = await Service().IngestAsync(again, GlFixtures.Tenant);

        result.Status.Should().Be(GlSourceEventStatus.Parked);
        result.ParkReason.Should().Be(GlParkReason.DuplicateBusinessKey);
        _gl.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task A_re_dated_superseded_file_does_not_post_cash_twice()
    {
        await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        await Service().IngestAsync(GlFixtures.FileTransmitted(fileReference: "FFS-PR-run-1"), GlFixtures.Tenant);

        var second = await Service().IngestAsync(GlFixtures.FileTransmitted(fileReference: "FFS-PR-run-1-R1"), GlFixtures.Tenant);

        second.ParkReason.Should().Be(GlParkReason.DuplicateBusinessKey);
        _gl.Entries.Count(e => e.Kind == GlEntryKind.AchTransmission).Should().Be(1);
        var report = GlReconciliationBuilder.Build(GlFixtures.Tenant, null, _gl.Events, _gl.Entries);
        report.Runs.Single().Flags.Should().Contain(GlReconciliationFlag.DuplicateTransmission);

        // Dismissed after review: no longer a difference.
        await Service().DismissAsync(GlFixtures.Tenant, second.EventId, "re-dated file R1 was never sent; payment-service shows R0 Transmitted", "controller-1");
        GlReconciliationBuilder.Build(GlFixtures.Tenant, null, _gl.Events, _gl.Entries).Runs.Single().Flags
            .Should().NotContain(GlReconciliationFlag.DuplicateTransmission);
    }

    [Fact]
    public async Task A_reversed_voided_payment_reverses_the_expense_into_a_provider_receivable()
    {
        await Service().IngestAsync(GlFixtures.RunExecuted(ach: 100m, check: 0m, offset: 0m), GlFixtures.Tenant);

        var result = await Service().IngestAsync(GlFixtures.ReversalExecuted(amount: 100m), GlFixtures.Tenant);

        result.Status.Should().Be(GlSourceEventStatus.Posted);
        var recoupment = _gl.Entries.Single(e => e.Kind == GlEntryKind.ClaimsRecoupment);
        Sum(recoupment, GlPostingRole.ProviderReceivable, debit: true).Should().Be(100m);
        Sum(recoupment, GlPostingRole.ClaimsExpense, debit: false).Should().Be(100m);
        var expense = _gl.Entries.SelectMany(e => e.Lines).Where(l => l.Role == GlPostingRole.ClaimsExpense).Sum(l => l.Debit - l.Credit);
        expense.Should().Be(0m);
    }

    [Fact]
    public async Task An_operator_reversal_mirrors_the_entry_once_and_never_edits_it()
    {
        await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        var original = _gl.Entries.Single();

        var reversal = await Service().ReverseAsync(GlFixtures.Tenant, original.Id, "posted to the wrong LOB", null, "controller-1");

        reversal.Kind.Should().Be(GlEntryKind.Reversal);
        reversal.ReversesEntryId.Should().Be(original.Id);
        reversal.Lines.Select(l => (l.Role, l.Debit, l.Credit)).Should().BeEquivalentTo(original.Lines.Select(l => (l.Role, l.Credit, l.Debit)));
        _gl.Entries.Single(e => e.Id == original.Id).Should().BeEquivalentTo(original);
        _gl.Entries.SelectMany(e => e.Lines).GroupBy(l => l.Role).Should().OnlyContain(g => g.Sum(l => l.Debit - l.Credit) == 0m);

        await FluentActions.Awaiting(() => Service().ReverseAsync(GlFixtures.Tenant, original.Id, "again", null, "controller-1"))
            .Should().ThrowAsync<GlStateException>();
        await FluentActions.Awaiting(() => Service().ReverseAsync(GlFixtures.Tenant, reversal.Id, "reverse the reversal", null, "controller-1"))
            .Should().ThrowAsync<GlStateException>();
        await FluentActions.Awaiting(() => Service().ReverseAsync(GlFixtures.Tenant, original.Id, " ", null, "controller-1"))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task An_event_dated_in_a_closed_period_is_parked_then_posted_on_a_later_open_date()
    {
        _clock.Now = new DateTimeOffset(2026, 6, 3, 9, 0, 0, TimeSpan.Zero);
        await Service().ClosePeriodAsync(GlFixtures.Tenant, "2026-05", "May closed", "controller-1");

        var parked = await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);

        parked.Status.Should().Be(GlSourceEventStatus.Parked);
        parked.ParkReason.Should().Be(GlParkReason.ClosedPeriod);
        _gl.Entries.Should().BeEmpty();
        _gl.Events.Should().ContainSingle(); // held, not dropped

        await FluentActions.Awaiting(() => Service().RetryAsync(GlFixtures.Tenant, parked.EventId, null, null, "controller-1"))
            .Should().NotThrowAsync(); // still closed: stays parked
        (await Service().RetryAsync(GlFixtures.Tenant, parked.EventId, null, null, "controller-1")).Status.Should().Be(GlSourceEventStatus.Parked);
        await FluentActions.Awaiting(() => Service().RetryAsync(GlFixtures.Tenant, parked.EventId, new DateTime(2026, 6, 1), null, "controller-1"))
            .Should().ThrowAsync<ArgumentException>("moving a date needs a reason");
        await FluentActions.Awaiting(() => Service().RetryAsync(GlFixtures.Tenant, parked.EventId, new DateTime(2026, 5, 1), "earlier", "controller-1"))
            .Should().ThrowAsync<ArgumentException>();

        var posted = await Service().RetryAsync(GlFixtures.Tenant, parked.EventId, new DateTime(2026, 6, 1), "May was closed before the run reached the GL", "controller-1");

        posted.Status.Should().Be(GlSourceEventStatus.Posted);
        var entry = _gl.Entries.Single();
        (entry.Period, entry.EntryDate).Should().Be(("2026-06", new DateTime(2026, 6, 1)));
        entry.Reason.Should().Contain("instead of 2026-05-20").And.Contain("controller-1");
    }

    [Fact]
    public async Task Closing_is_idempotent_and_never_for_a_future_period()
    {
        (await Service().ClosePeriodAsync(GlFixtures.Tenant, "2026-04", "April", "c-1")).Period.Should().Be("2026-04");
        await Service().ClosePeriodAsync(GlFixtures.Tenant, "2026-04", "again", "c-2");
        await FluentActions.Awaiting(() => Service().ClosePeriodAsync(GlFixtures.Tenant, "2026-07", "early", "c-1")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Service().ClosePeriodAsync(GlFixtures.Tenant, "2026-13", "bad", "c-1")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Service().ReverseAsync(GlFixtures.Tenant, "x", "r", new DateTime(2026, 4, 2), "c-1")).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task A_missing_mapping_parks_the_event_until_it_is_mapped()
    {
        _options = GlFixtures.Options(unmapped: GlPostingRole.ProviderReceivable);

        var parked = await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);

        parked.Status.Should().Be(GlSourceEventStatus.Parked);
        parked.ParkReason.Should().Be(GlParkReason.NeedsMapping);
        parked.ParkDetail.Should().Contain("ProviderReceivable");
        _gl.Entries.Should().BeEmpty();

        _options = GlFixtures.Options();
        (await Service().RetryAsync(GlFixtures.Tenant, parked.EventId, null, null, "controller-1")).Status.Should().Be(GlSourceEventStatus.Posted);
        _gl.Entries.Should().ContainSingle();
        _gl.Events.Single().Attempts.Should().Be(2);
    }

    [Theory]
    [InlineData(GlAccountStatus.Inactive, null, null)]
    [InlineData(GlAccountStatus.Active, "2026-06-01", null)]   // not yet effective
    [InlineData(GlAccountStatus.Active, null, "2026-05-01")]   // terminated
    public async Task An_account_that_cannot_take_postings_parks_the_event(GlAccountStatus status, string? effective, string? terminated)
    {
        var payable = _gl.Chart.Single(a => a.AccountNumber == "2100");
        payable.Status = status;
        if (effective != null) payable.EffectiveDate = DateTime.Parse(effective);
        if (terminated != null) payable.TerminationDate = DateTime.Parse(terminated);

        var result = await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);

        result.ParkReason.Should().Be(GlParkReason.NeedsMapping);
        _gl.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_duplicated_account_number_in_the_chart_is_never_guessed()
    {
        _gl.Chart.Add(new GlAccount { Id = "dup", TenantId = GlFixtures.Tenant, AccountNumber = "2100", Status = GlAccountStatus.Active, EffectiveDate = new DateTime(2020, 1, 1) });

        (await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant)).ParkReason.Should().Be(GlParkReason.NeedsMapping);
    }

    [Fact]
    public async Task Another_tenants_mapping_and_chart_are_never_used()
    {
        var other = GlFixtures.RunExecuted() with { TenantId = "tenant-2", EventId = Guid.NewGuid().ToString() };

        var result = await Service().IngestAsync(other, "tenant-2");

        result.ParkReason.Should().Be(GlParkReason.NeedsMapping);
        await FluentActions.Awaiting(() => Service().IngestAsync(GlFixtures.RunExecuted(), "tenant-2")).Should().ThrowAsync<GlForbiddenException>();
    }

    [Theory]
    [InlineData(-5)]       // negative
    [InlineData(10.005)]   // not money
    public async Task A_payload_that_is_not_money_is_parked_not_posted(double amount)
    {
        var result = await Service().IngestAsync(GlFixtures.RunExecuted(ach: (decimal)amount), GlFixtures.Tenant);

        result.ParkReason.Should().Be(GlParkReason.InvalidPayload);
        _gl.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_payload_whose_totals_do_not_add_up_is_parked()
    {
        var envelope = GlFixtures.RunExecuted();
        var tampered = envelope with { EventId = Guid.NewGuid().ToString(), PayloadJson = envelope.PayloadJson.Replace("\"totalNetAmount\":150", "\"totalNetAmount\":151") };

        (await Service().IngestAsync(tampered, GlFixtures.Tenant)).ParkReason.Should().Be(GlParkReason.InvalidPayload);
        _gl.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_with_debits_is_refused()
    {
        var envelope = GlFixtures.FileTransmitted();
        var debits = envelope with { PayloadJson = envelope.PayloadJson.Replace("\"totalDebitAmount\":0", "\"totalDebitAmount\":5") };

        (await Service().IngestAsync(debits, GlFixtures.Tenant)).ParkReason.Should().Be(GlParkReason.InvalidPayload);
    }

    [Fact]
    public async Task Zero_amounts_post_nothing_but_are_recorded()
    {
        var result = await Service().IngestAsync(GlFixtures.RunExecuted(ach: 0m, check: 0m, offset: 0m), GlFixtures.Tenant);

        result.Status.Should().Be(GlSourceEventStatus.NothingToPost);
        _gl.Entries.Should().BeEmpty();
        _gl.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task Disabled_stores_and_posts_nothing()
    {
        _options = GlFixtures.Options(enabled: false);

        await FluentActions.Awaiting(() => Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant)).Should().ThrowAsync<GlPostingDisabledException>();
        await FluentActions.Awaiting(() => Service().ClosePeriodAsync(GlFixtures.Tenant, "2026-04", "x", "c")).Should().ThrowAsync<GlPostingDisabledException>();

        _gl.Entries.Should().BeEmpty();
        _gl.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_types_and_unsafe_ids_are_rejected()
    {
        var envelope = GlFixtures.RunExecuted();

        await FluentActions.Awaiting(() => Service().IngestAsync(envelope with { Type = "PaymentRunDeleted" }, GlFixtures.Tenant)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Service().IngestAsync(envelope with { EventId = "../x" }, GlFixtures.Tenant)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Service().IngestAsync(envelope with { EventId = "" }, GlFixtures.Tenant)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void The_journal_refuses_an_unbalanced_or_one_sided_entry()
    {
        GlJournalEntry Entry(params (decimal Debit, decimal Credit)[] lines) => new()
        {
            Id = "e",
            TenantId = GlFixtures.Tenant,
            Lines = lines.Select((l, i) => new GlJournalLine { LineNumber = i + 1, AccountId = "a", AccountNumber = "1", Debit = l.Debit, Credit = l.Credit }).ToList(),
            TotalDebit = lines.Sum(l => l.Debit),
            TotalCredit = lines.Sum(l => l.Credit),
        };

        FluentActions.Invoking(() => { _gl.TryInsertAsync(Entry((10m, 0m), (0m, 9.99m))); }).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => GlJournal.EnsureBalanced(Entry((10m, 0m)))).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => GlJournal.EnsureBalanced(Entry((10m, 10m), (0m, 0m)))).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => GlJournal.EnsureBalanced(Entry((10.001m, 0m), (0m, 10.001m)))).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => GlJournal.EnsureBalanced(Entry((-1m, 0m), (0m, -1m)))).Should().Throw<InvalidOperationException>();
        var badTotals = Entry((10m, 0m), (0m, 10m));
        badTotals.TotalDebit = 11m;
        FluentActions.Invoking(() => GlJournal.EnsureBalanced(badTotals)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => GlJournal.EnsureBalanced(Entry((10m, 0m), (0m, 10m)))).Should().NotThrow();
    }

    [Fact]
    public async Task The_reconciliation_ties_gl_payable_to_runs_and_files()
    {
        await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);          // 150 payable (100 ACH, 50 check)
        await Service().IngestAsync(GlFixtures.FileTransmitted(amount: 100m), GlFixtures.Tenant);
        // A transmitted file whose run never reached the GL.
        await Service().IngestAsync(GlFixtures.FileTransmitted(runId: "run-9", fileReference: "FFS-PR-run-9", amount: 30m), GlFixtures.Tenant);

        var report = GlReconciliationBuilder.Build(GlFixtures.Tenant, null, _gl.Events, _gl.Entries);

        var run1 = report.Runs.Single(r => r.PaymentRunId == "run-1");
        (run1.RunNetTotal, run1.RunAchTotal, run1.RunCheckTotal, run1.AccruedPayable, run1.TransmittedPayable, run1.OutstandingPayable)
            .Should().Be((150m, 100m, 50m, 150m, 100m, 50m));
        run1.Flags.Should().Equal(GlReconciliationFlag.PayableOutstanding);
        var run9 = report.Runs.Single(r => r.PaymentRunId == "run-9");
        run9.Flags.Should().Contain(GlReconciliationFlag.AccrualMissing).And.Contain(GlReconciliationFlag.TransmittedMoreThanAccrued);
        report.GlClaimsPayableBalance.Should().Be(150m - 100m - 30m);
        report.RunsOutstandingPayable.Should().Be(50m - 30m);
        report.Difference.Should().Be(0m);
        report.Balanced.Should().BeFalse("run-9 needs a person");
    }

    [Fact]
    public async Task The_reconciliation_flags_a_file_that_differs_from_the_runs_ach_payments_and_parked_events()
    {
        _options = GlFixtures.Options(unmapped: GlPostingRole.AchInTransit);
        await Service().IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        await Service().IngestAsync(GlFixtures.FileTransmitted(amount: 90m), GlFixtures.Tenant);

        var run = GlReconciliationBuilder.Build(GlFixtures.Tenant, "2026-05", _gl.Events, _gl.Entries).Runs.Single();

        run.Flags.Should().Contain(GlReconciliationFlag.TransmissionParked).And.Contain(GlReconciliationFlag.FileDiffersFromAchPayments);
    }
}
