using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;
using PaymentService.Repositories;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// Provider receivables: opened by a reversal's negative 835 balance (PLB FB),
/// recovered by later payment runs as a positive PLB offset that lowers BPR02
/// and the EFT credit, across runs and partially, never below zero, with an
/// append-only audited history and aging.
/// </summary>
public class ProviderReceivableRecoveryTests
{
    private const string Npi = "1111111111";

    [Fact]
    public async Task A_receivable_is_recovered_across_two_runs_and_each_835_balances()
    {
        var h = new FfsRunHarness();
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        var receivable = await h.SeedReceivableAsync(Npi, 650m);

        // Run 1: 400.00 of claims, all of it withheld; 250.00 still owed.
        var run1 = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 400m));

        Assert.Equal(PaymentRunStatus.Completed, run1.Status);
        Assert.Equal(0m, run1.TotalPaymentAmount);
        Assert.Equal(400m, run1.ReceivableRecoveredAmount);
        var recovery1 = Assert.Single(run1.ReceivableRecoveries);
        Assert.Equal((receivable.Id, 400m, "FB", "R-ABC12345"), (recovery1.ReceivableId, recovery1.Amount, recovery1.AdjustmentCode, recovery1.Reference));
        AssertEnvelopeBalances(h.Envelopes[0], bpr02: 0m, clp04: 400m, plb: 400m, expectedPlb: "FB:R-ABC12345");

        var afterRun1 = (await h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!;
        Assert.Equal((250m, 400m, ReceivableStatus.PartiallyRecovered), (afterRun1.OutstandingAmount, afterRun1.RecoveredAmount, afterRun1.Status));

        // Run 2: 300.00 of claims; only the 250.00 still owed is withheld.
        var run2 = await h.ExecuteRunAsync(FfsRunHarness.Claim("c2", Npi, 300m));

        Assert.Equal(50m, run2.TotalPaymentAmount);
        Assert.Equal(250m, Assert.Single(run2.ReceivableRecoveries).Amount);
        AssertEnvelopeBalances(h.Envelopes[1], bpr02: 50m, clp04: 300m, plb: 250m, expectedPlb: "FB:R-ABC12345");

        var final = (await h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!;
        Assert.Equal((0m, 650m, ReceivableStatus.Recovered), (final.OutstandingAmount, final.RecoveredAmount, final.Status));
        Assert.NotNull(final.FullyRecoveredAt);

        // The history is append-only and names the runs, payments and approver.
        Assert.Equal(
            new[] { (ReceivableEntryType.Originated, 650m, 650m), (ReceivableEntryType.Recovered, 400m, 250m), (ReceivableEntryType.Recovered, 250m, 0m) },
            final.Entries.Select(e => (e.Type, e.Amount, e.OutstandingAfter)));
        Assert.Equal(new[] { "run-1", "run-2" }, final.Entries.Skip(1).Select(e => e.RunId));
        Assert.Equal(h.Payments.All.Select(p => p.Id).OrderBy(x => x), final.Entries.Skip(1).Select(e => e.PaymentId!).OrderBy(x => x));
        Assert.All(final.Entries.Skip(1), e => Assert.Equal("approver-1", e.By));

        // A third run withholds nothing: the receivable is closed.
        var run3 = await h.ExecuteRunAsync(FfsRunHarness.Claim("c3", Npi, 75m));
        Assert.Equal(75m, run3.TotalPaymentAmount);
        Assert.Empty(run3.ReceivableRecoveries);
        Assert.DoesNotContain(FfsRunHarness.Segments(h.Envelopes[2]), s => s[0] == "PLB");
    }

    [Fact]
    public async Task A_partial_recovery_leaves_the_rest_outstanding_and_the_payment_carries_the_offset()
    {
        var h = new FfsRunHarness();
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        var receivable = await h.SeedReceivableAsync(Npi, 650m);

        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m));

        Assert.Equal(0m, run.TotalPaymentAmount);
        var payment = Assert.Single(h.Payments.All);
        Assert.Equal(0m, payment.TotalPaymentAmount);
        var offset = Assert.Single(payment.ReceivableOffsets);
        Assert.Equal((receivable.Id, 100m), (offset.ReceivableId, offset.Amount));
        var plb = Assert.Single(payment.ProviderAdjustments);
        Assert.Equal(("FB", "R-ABC12345", 100m), (plb.AdjustmentIdentifier, plb.ReferenceIdentification, plb.Amount));
        // The claim itself is still paid in full (CLP04): the offset is provider-level.
        Assert.Equal(100m, Assert.Single(payment.ClaimPayments).PaymentAmount);

        var stored = (await h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!;
        Assert.Equal((550m, 100m, ReceivableStatus.PartiallyRecovered), (stored.OutstandingAmount, stored.RecoveredAmount, stored.Status));
    }

    [Fact]
    public async Task Oldest_receivable_is_recovered_first_and_each_gets_its_own_plb()
    {
        var h = new FfsRunHarness();
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        var older = await h.SeedReceivableAsync(Npi, 30m, trace: "R-OLD", envelopeId: "env-rev-old");
        await Task.Delay(5);
        var newer = await h.SeedReceivableAsync(Npi, 100m, trace: "R-NEW", envelopeId: "env-rev-new");

        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 80m));

        Assert.Equal(new[] { (older.Id, 30m), (newer.Id, 50m) }, run.ReceivableRecoveries.Select(r => (r.ReceivableId, r.Amount)));
        Assert.Equal(0m, run.TotalPaymentAmount);
        var plb = FfsRunHarness.Segments(h.Envelopes.Single()).Single(s => s[0] == "PLB");
        Assert.Equal(new[] { "FB:R-OLD", "30.00", "FB:R-NEW", "50.00" }, plb.Skip(3));
        Assert.Equal(ReceivableStatus.Recovered, (await h.Ledger.GetAsync(FfsRunHarness.Tenant, older.Id))!.Status);
        Assert.Equal(50m, (await h.Ledger.GetAsync(FfsRunHarness.Tenant, newer.Id))!.OutstandingAmount);
    }

    [Fact]
    public async Task Another_providers_payment_recovers_nothing()
    {
        var h = new FfsRunHarness();
        h.Partner("2222222222", "TP-B");
        h.Accounts.Eft("2222222222");
        await h.SeedReceivableAsync(Npi, 650m);

        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", "2222222222", 100m));

        Assert.Equal(100m, run.TotalPaymentAmount);
        Assert.Empty(run.ReceivableRecoveries);
    }

    [Fact]
    public void A_recovery_larger_than_the_outstanding_balance_is_refused()
    {
        var record = new ProviderReceivableRecord { Id = "r", OriginalAmount = 100m, OutstandingAmount = 40m, RecoveredAmount = 60m };

        Assert.Throws<InvalidOperationException>(() => record.ApplyRecovery(40.01m, "run", "PR", "p", "T", "u", DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() => record.ApplyRecovery(0m, "run", "PR", "p", "T", "u", DateTime.UtcNow));
        Assert.Equal(40m, record.OutstandingAmount);
        Assert.Empty(record.Entries);

        record.ApplyRecovery(40m, "run", "PR", "p", "T", "u", DateTime.UtcNow);
        Assert.Equal((0m, ReceivableStatus.Recovered), (record.OutstandingAmount, record.Status));
        Assert.Throws<InvalidOperationException>(() => record.ApplyRecovery(0.01m, "run", "PR", "p2", "T", "u", DateTime.UtcNow));
    }

    [Fact]
    public async Task A_concurrent_recovery_never_takes_the_receivable_below_zero()
    {
        // Another run recovers 80.00 between this run's read and its write: the
        // conditional write fails, the ledger is re-read, and only what is left is taken.
        var store = new InterferingReceivableStore();
        var h = new FfsRunHarness(store);
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        var receivable = await h.SeedReceivableAsync(Npi, 100m);
        store.BeforeFirstReplace = async () =>
        {
            var other = (await store.Inner.GetAsync(FfsRunHarness.Tenant, receivable.Id))!;
            var expected = other.Version;
            other.ApplyRecovery(80m, "run-other", "PR-OTHER", "pay-other", "T-OTHER", "someone", DateTime.UtcNow);
            other.Version = expected + 1;
            Assert.True(await store.Inner.TryReplaceAsync(other, expected));
        };

        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 90m));

        Assert.Equal(20m, Assert.Single(run.ReceivableRecoveries).Amount);
        Assert.Equal(70m, run.TotalPaymentAmount);
        var stored = (await h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!;
        Assert.Equal((0m, 100m), (stored.OutstandingAmount, stored.RecoveredAmount));
        Assert.All(stored.Entries, e => Assert.True(e.OutstandingAfter >= 0m));
    }

    [Fact]
    public async Task When_the_payment_cannot_be_inserted_the_recovery_is_reversed_in_the_ledger()
    {
        var h = new FfsRunHarness();
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        var receivable = await h.SeedReceivableAsync(Npi, 650m);
        h.Payments.FailNextCreate = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 400m)));

        var stored = (await h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!;
        Assert.Equal((650m, 0m, ReceivableStatus.Open), (stored.OutstandingAmount, stored.RecoveredAmount, stored.Status));
        Assert.Equal(
            new[] { ReceivableEntryType.Originated, ReceivableEntryType.Recovered, ReceivableEntryType.RecoveryReversed },
            stored.Entries.Select(e => e.Type));
    }

    [Fact]
    public async Task The_eft_credit_is_the_payment_net_of_the_offset()
    {
        var h = new FfsRunHarness();
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        await h.SeedReceivableAsync(Npi, 250m);

        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 300m));
        var outcome = await h.EftFiles().GenerateAsync(run.Id, "approver-2");

        var entry = Assert.Single(outcome.Run.EftFile!.Entries);
        Assert.Equal(50m, entry.Amount);
        Assert.Equal(50m, outcome.Run.EftFile.TotalCreditAmount);
        var bpr02 = decimal.Parse(FfsRunHarness.Segments(h.Envelopes.Single()).Single(s => s[0] == "BPR")[2]);
        Assert.Equal(bpr02, entry.Amount);
    }

    [Fact]
    public async Task A_fully_offset_payment_is_not_credited()
    {
        var h = new FfsRunHarness();
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        await h.SeedReceivableAsync(Npi, 650m);

        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 400m));
        var outcome = await h.EftFiles().GenerateAsync(run.Id, "approver-2");

        Assert.Null(outcome.File);
        Assert.Empty(outcome.Run.EftFile!.Entries);
        Assert.Equal(new[] { Assert.Single(h.Payments.All).Id }, outcome.Run.EftFile.ZeroAmountPaymentIds);
    }

    [Fact]
    public async Task Recording_the_same_forward_balance_twice_opens_one_receivable()
    {
        var h = new FfsRunHarness();

        var first = await h.SeedReceivableAsync(Npi, 650m);
        var second = await h.SeedReceivableAsync(Npi, 650m);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(h.LedgerStore.All);
    }

    [Fact]
    public async Task Aging_buckets_the_outstanding_balance_by_days_since_it_arose()
    {
        var store = new InMemoryProviderReceivableRepository();
        var ledger = new ProviderReceivableLedger(store, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<ProviderReceivableLedger>.Instance);
        var asOf = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        async Task Seed(string id, string npi, decimal outstanding, int ageDays)
            => await store.CreateIfAbsentAsync(new ProviderReceivableRecord
            {
                Id = id, TenantId = "t", ProviderNpi = npi, OriginalAmount = outstanding + 10m, OutstandingAmount = outstanding,
                RecoveredAmount = 10m, Status = ReceivableStatus.PartiallyRecovered, OriginatedAt = asOf.AddDays(-ageDays),
            });
        await Seed("a", "N1", 100m, 5);
        await Seed("b", "N1", 200m, 45);
        await Seed("c", "N2", 300m, 75);
        await Seed("d", "N2", 400m, 100);
        await Seed("e", "N2", 500m, 200);
        await store.CreateIfAbsentAsync(new ProviderReceivableRecord
        {
            Id = "closed", TenantId = "t", ProviderNpi = "N1", OriginalAmount = 99m, OutstandingAmount = 0m, RecoveredAmount = 99m,
            Status = ReceivableStatus.Recovered, OriginatedAt = asOf.AddDays(-300),
        });

        var report = await ledger.GetAgingAsync("t", asOf);

        Assert.Equal(1500m, report.TotalOutstanding);
        Assert.Equal(new[] { 100m, 200m, 300m, 400m, 500m },
            Enum.GetValues<ReceivableAgingBucket>().Select(b => report.Buckets[b]));
        var n2 = report.Providers.Single(p => p.ProviderNpi == "N2");
        Assert.Equal((1200m, 3, 200), (n2.TotalOutstanding, n2.OpenReceivables, n2.OldestAgeDays));
        Assert.Equal(2, report.Providers.Single(p => p.ProviderNpi == "N1").OpenReceivables);
    }

    // ── review fixes: rollback, landed writes, retries ────────────────

    private static Payment PaymentFor(string npi, decimal amount, string id = "pay-1") => new()
    {
        Id = id, PayeeNPI = npi, CheckNumber = "0001000001", TotalPaymentAmount = amount,
        PaymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task A_failure_on_the_second_receivable_rolls_back_the_first_and_leaves_the_payment_untouched()
    {
        var store = new ScriptedReceivableStore();
        var h = new FfsRunHarness(store);
        var first = await h.SeedReceivableAsync(Npi, 30m, trace: "R-1", envelopeId: "env-1");
        await Task.Delay(5);
        var second = await h.SeedReceivableAsync(Npi, 100m, trace: "R-2", envelopeId: "env-2");
        store.OnReplace = (record, _) => record.Id == second.Id && record.Entries.Last().Type == ReceivableEntryType.Recovered
            ? throw new TimeoutException("mongo timeout")
            : null;
        var payment = PaymentFor(Npi, 80m);

        await Assert.ThrowsAsync<TimeoutException>(() => h.Ledger.ApplyRecoveriesAsync(FfsRunHarness.Tenant, payment, "run-x", "PR-X", "approver-1"));

        Assert.Equal(80m, payment.TotalPaymentAmount);
        Assert.Empty(payment.ReceivableOffsets);
        Assert.Empty(payment.ProviderAdjustments);
        var firstAfter = (await store.Inner.GetAsync(FfsRunHarness.Tenant, first.Id))!;
        Assert.Equal((30m, ReceivableStatus.Open), (firstAfter.OutstandingAmount, firstAfter.Status));
        Assert.Equal(new[] { ReceivableEntryType.Originated, ReceivableEntryType.Recovered, ReceivableEntryType.RecoveryReversed },
            firstAfter.Entries.Select(e => e.Type));
        Assert.Equal(100m, (await store.Inner.GetAsync(FfsRunHarness.Tenant, second.Id))!.OutstandingAmount);
    }

    [Fact]
    public async Task A_partial_recovery_failure_in_a_run_issues_no_payment_and_leaves_the_ledger_whole()
    {
        var store = new ScriptedReceivableStore();
        var h = new FfsRunHarness(store);
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        var first = await h.SeedReceivableAsync(Npi, 30m, trace: "R-1", envelopeId: "env-1");
        await Task.Delay(5);
        var second = await h.SeedReceivableAsync(Npi, 100m, trace: "R-2", envelopeId: "env-2");
        store.OnReplace = (record, _) => record.Id == second.Id ? Task.FromResult(false) : null; // conflicts forever

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 80m)));

        Assert.Empty(h.Payments.All);
        Assert.Equal(30m, (await store.Inner.GetAsync(FfsRunHarness.Tenant, first.Id))!.OutstandingAmount);
        Assert.Equal(100m, (await store.Inner.GetAsync(FfsRunHarness.Tenant, second.Id))!.OutstandingAmount);
    }

    [Fact]
    public async Task A_payment_insert_that_landed_despite_a_timeout_keeps_its_recoveries()
    {
        var h = new FfsRunHarness();
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        var receivable = await h.SeedReceivableAsync(Npi, 650m);
        h.Payments.StoreThenFailNextCreate = true;

        await Assert.ThrowsAsync<TimeoutException>(() => h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 400m)));

        var stored = Assert.Single(h.Payments.All);
        Assert.Equal(400m, Assert.Single(stored.ReceivableOffsets).Amount);
        var ledger = (await h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!;
        Assert.Equal(250m, ledger.OutstandingAmount);
        Assert.DoesNotContain(ledger.Entries, e => e.Type == ReceivableEntryType.RecoveryReversed);
    }

    [Fact]
    public async Task A_failing_reversal_after_a_failed_insert_does_not_hide_the_insert_error()
    {
        var store = new ScriptedReceivableStore();
        var h = new FfsRunHarness(store);
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        await h.SeedReceivableAsync(Npi, 650m);
        store.OnReplace = (record, _) => record.Entries.Last().Type == ReceivableEntryType.RecoveryReversed
            ? throw new TimeoutException("mongo timeout on reversal")
            : null;
        h.Payments.FailNextCreate = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 400m)));

        Assert.Equal("payment store unavailable", ex.Message);
    }

    [Fact]
    public async Task A_retried_recovery_that_closed_the_receivable_returns_the_same_offset()
    {
        var h = new FfsRunHarness();
        var receivable = await h.SeedReceivableAsync(Npi, 100m);
        await h.Ledger.ApplyRecoveriesAsync(FfsRunHarness.Tenant, PaymentFor(Npi, 100m), "run-y", "PR-Y", "approver-1");
        Assert.Equal(ReceivableStatus.Recovered, (await h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!.Status);

        // The same payment asks again (a retried step) and a stale listing still
        // offers the now-closed receivable: its recovery by this payment is
        // reported back, not dropped as "nothing outstanding", and not applied twice.
        var again = PaymentFor(Npi, 100m);
        var replay = await new ProviderReceivableLedger(new StaleListingStore(h.LedgerStore, receivable.Id), h.Configuration,
            NullLogger<ProviderReceivableLedger>.Instance).ApplyRecoveriesAsync(FfsRunHarness.Tenant, again, "run-y", "PR-Y", "approver-1");

        Assert.Equal((receivable.Id, 100m), (Assert.Single(replay).ReceivableId, replay[0].Amount));
        Assert.Equal(0m, again.TotalPaymentAmount);
        var stored = (await h.Ledger.GetAsync(FfsRunHarness.Tenant, receivable.Id))!;
        Assert.Equal(1, stored.Entries.Count(e => e.Type == ReceivableEntryType.Recovered));
        Assert.Equal(0m, stored.OutstandingAmount);
    }

    [Fact]
    public async Task Reversing_a_recovery_that_keeps_conflicting_throws_instead_of_exiting_silently()
    {
        var store = new ScriptedReceivableStore();
        var h = new FfsRunHarness(store);
        await h.SeedReceivableAsync(Npi, 50m);
        var payment = PaymentFor(Npi, 80m);
        await h.Ledger.ApplyRecoveriesAsync(FfsRunHarness.Tenant, payment, "run-x", "PR-X", "approver-1");
        store.OnReplace = (_, _) => Task.FromResult(false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ledger.ReverseRecoveriesAsync(FfsRunHarness.Tenant, payment, "approver-1", "test"));

        Assert.Contains("could not be reversed", ex.Message);
    }

    /// <summary>A store whose conditional writes a test can fail, refuse or let through (null).</summary>
    private sealed class ScriptedReceivableStore : IProviderReceivableRepository
    {
        public InMemoryProviderReceivableRepository Inner { get; } = new();
        public Func<ProviderReceivableRecord, long, Task<bool>?>? OnReplace { get; set; }

        public Task<ProviderReceivableRecord> CreateIfAbsentAsync(ProviderReceivableRecord record) => Inner.CreateIfAbsentAsync(record);
        public Task<ProviderReceivableRecord?> GetAsync(string tenantId, string id) => Inner.GetAsync(tenantId, id);
        public Task<IReadOnlyList<ProviderReceivableRecord>> ListOutstandingAsync(string tenantId, string providerNpi) => Inner.ListOutstandingAsync(tenantId, providerNpi);
        public Task<IReadOnlyList<ProviderReceivableRecord>> SearchAsync(string tenantId, string? providerNpi = null, ReceivableStatus? status = null) => Inner.SearchAsync(tenantId, providerNpi, status);

        public Task<bool> TryReplaceAsync(ProviderReceivableRecord record, long expectedVersion)
            => OnReplace?.Invoke(record, expectedVersion) ?? Inner.TryReplaceAsync(record, expectedVersion);
    }

    /// <summary>Lists one receivable as outstanding even once it is closed (a stale read).</summary>
    private sealed class StaleListingStore : IProviderReceivableRepository
    {
        private readonly IProviderReceivableRepository _inner;
        private readonly string _id;

        public StaleListingStore(IProviderReceivableRepository inner, string id) { _inner = inner; _id = id; }

        public Task<ProviderReceivableRecord> CreateIfAbsentAsync(ProviderReceivableRecord record) => _inner.CreateIfAbsentAsync(record);
        public Task<ProviderReceivableRecord?> GetAsync(string tenantId, string id) => _inner.GetAsync(tenantId, id);
        public async Task<IReadOnlyList<ProviderReceivableRecord>> ListOutstandingAsync(string tenantId, string providerNpi)
            => new[] { (await _inner.GetAsync(tenantId, _id))! };
        public Task<IReadOnlyList<ProviderReceivableRecord>> SearchAsync(string tenantId, string? providerNpi = null, ReceivableStatus? status = null) => _inner.SearchAsync(tenantId, providerNpi, status);
        public Task<bool> TryReplaceAsync(ProviderReceivableRecord record, long expectedVersion) => _inner.TryReplaceAsync(record, expectedVersion);
    }

    private static void AssertEnvelopeBalances(EraEnvelopeRecord envelope, decimal bpr02, decimal clp04, decimal plb, string expectedPlb)
    {
        var segments = FfsRunHarness.Segments(envelope);
        Assert.Equal(bpr02, decimal.Parse(segments.Single(s => s[0] == "BPR")[2]));
        Assert.Equal(clp04, segments.Where(s => s[0] == "CLP").Sum(s => decimal.Parse(s[4])));
        var plbSegment = segments.Single(s => s[0] == "PLB");
        Assert.Equal(expectedPlb, plbSegment[3]);
        Assert.Equal(plb, decimal.Parse(plbSegment[4]));
        Assert.Equal(bpr02, clp04 - plb);
    }

    /// <summary>Lets a test act between a recovery's read and its conditional write.</summary>
    private sealed class InterferingReceivableStore : IProviderReceivableRepository
    {
        public InMemoryProviderReceivableRepository Inner { get; } = new();
        public Func<Task>? BeforeFirstReplace { get; set; }

        public Task<ProviderReceivableRecord> CreateIfAbsentAsync(ProviderReceivableRecord record) => Inner.CreateIfAbsentAsync(record);
        public Task<ProviderReceivableRecord?> GetAsync(string tenantId, string id) => Inner.GetAsync(tenantId, id);
        public Task<IReadOnlyList<ProviderReceivableRecord>> ListOutstandingAsync(string tenantId, string providerNpi) => Inner.ListOutstandingAsync(tenantId, providerNpi);
        public Task<IReadOnlyList<ProviderReceivableRecord>> SearchAsync(string tenantId, string? providerNpi = null, ReceivableStatus? status = null) => Inner.SearchAsync(tenantId, providerNpi, status);

        public async Task<bool> TryReplaceAsync(ProviderReceivableRecord record, long expectedVersion)
        {
            if (BeforeFirstReplace is { } hook)
            {
                BeforeFirstReplace = null;
                await hook();
            }
            return await Inner.TryReplaceAsync(record, expectedVersion);
        }
    }
}
