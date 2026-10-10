using CloudHealthOffice.BankReconciliation.Testing;
using CloudHealthOffice.FieldProtection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CloudHealthOffice.BankReconciliation.Testing.Bai2Builder;

namespace CloudHealthOffice.BankReconciliation.Tests;

internal sealed class SettableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class RecordingSink : IBankReconciliationEventSink
{
    public List<BankReconciliationEvent> Events { get; } = [];
    public bool Fail { get; set; }

    public Task PublishAsync(BankReconciliationEvent evt, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("sink down");
        Events.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>A matcher that decides from the type code, counting how often it is asked.</summary>
internal sealed class CountingMatcher : IBankTransactionMatcher
{
    public int Calls { get; private set; }

    public Task<MatchDecision> DecideAsync(BankTransaction tx, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(tx.TypeCode switch
        {
            "165" => new MatchDecision("PremiumCash", ReconciliationStatus.Matched, "matched", "Thing", ["thing-1"]),
            "555" => new MatchDecision("AchReturn", ReconciliationStatus.Return, "return"),
            _ => new MatchDecision("PremiumCash", ReconciliationStatus.Suspense, "no match"),
        });
    }
}

public class IngestionAndRunnerTests
{
    private const string Tenant = "tenant-bai";
    private readonly InMemoryBankReconciliationStore _store = new();
    private readonly SettableClock _clock = new(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));
    private readonly RecordingSink _sink = new();
    private readonly IFieldProtector _protector = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), "bai2-tests");

    private IOptions<BankReconciliationOptions> Options(Action<BankReconciliationOptions>? edit = null)
    {
        var o = new BankReconciliationOptions { ServiceName = "test-service" };
        edit?.Invoke(o);
        return Microsoft.Extensions.Options.Options.Create(o);
    }

    private BankStatementIngestion Ingestion() => new(_store, _protector, Options(), NullLogger<BankStatementIngestion>.Instance, _clock);

    private BankReconciliationRunner Runner(Action<BankReconciliationOptions>? edit = null) =>
        new(_store, _sink, Options(edit), NullLogger<BankReconciliationRunner>.Instance, _clock);

    private static Bai2Builder ThreeLines(string fileId = "1") => new()
    {
        FileId = fileId,
        Groups =
        [
            new Group
            {
                Accounts =
                [
                    new Account
                    {
                        Number = "9988776655",
                        Transactions =
                        [
                            new Tx("165", 125000, CustomerReference: "INV-1"),
                            new Tx("169", 5000, CustomerReference: "UNKNOWN"),
                            new Tx("555", 2500, Text: "RETURN R01"),
                        ],
                    },
                ],
            },
        ],
    };

    [Fact]
    public async Task Ingest_StoresFileEncrypted_AndTransactionsMasked()
    {
        var result = await Ingestion().IngestAsync(Tenant, ThreeLines().Bytes(), "stmt.bai", "user-1");

        result.Status.Should().Be(BankStatementIngestStatus.Ingested);
        result.Transactions.Should().HaveCount(3);
        result.Transactions.Should().OnlyContain(t => t.MaskedAccount == "****6655" && t.AccountSuffix == "6655");
        result.Transactions[0].Amount.Should().Be(1250.00m);
        result.Statement.ProtectedContent.Should().BeEmpty("responses never carry the file");

        var stored = await _store.GetFileAsync(Tenant, result.Statement.Id);
        stored!.ProtectedContent.Should().StartWith("enc:").And.NotContain("9988776655");
        _protector.Unprotect(stored.ProtectedContent, new FieldProtectionContext(Tenant, stored.Id, BankStatementIngestion.ContentField))
            .Should().Contain("9988776655");
        stored.BankFileKey.Should().Be("SYNBANK01|CHOTENANT|20261010|1");
        stored.MaskedAccounts.Should().Equal("****6655");
    }

    [Fact]
    public async Task SameFileTwice_IsADuplicate_WithItsTransactions()
    {
        var bytes = ThreeLines().Bytes();
        var first = await Ingestion().IngestAsync(Tenant, bytes, "a.bai", null);
        var second = await Ingestion().IngestAsync(Tenant, bytes, "a-again.bai", null);

        second.Status.Should().Be(BankStatementIngestStatus.Duplicate);
        second.Statement.Id.Should().Be(first.Statement.Id);
        second.Transactions.Select(t => t.Id).Should().Equal(first.Transactions.Select(t => t.Id));
        (await _store.ListFilesAsync(Tenant, 10)).Should().ContainSingle();
    }

    [Fact]
    public async Task SameFileForAnotherTenant_IsItsOwnFile()
    {
        var bytes = ThreeLines().Bytes();
        await Ingestion().IngestAsync(Tenant, bytes, null, null);
        (await Ingestion().IngestAsync("other-tenant", bytes, null, null)).Status.Should().Be(BankStatementIngestStatus.Ingested);
    }

    [Fact]
    public async Task DifferentFileReusingTheBankFileId_IsRejected()
    {
        await Ingestion().IngestAsync(Tenant, ThreeLines().Bytes(), null, null);

        var changed = ThreeLines();
        changed.Groups[0].Accounts[0].Transactions.Add(new Tx("165", 1, CustomerReference: "EXTRA"));
        var result = await Ingestion().IngestAsync(Tenant, changed.Bytes(), null, null);

        result.Status.Should().Be(BankStatementIngestStatus.Rejected);
        result.Errors.Single().Should().Contain("already used by a different file");
        result.Transactions.Should().BeEmpty();

        // A corrected file with a new file id is accepted.
        var corrected = ThreeLines(fileId: "2");
        corrected.Groups[0].Accounts[0].Transactions.Add(new Tx("165", 1, CustomerReference: "EXTRA"));
        (await Ingestion().IngestAsync(Tenant, corrected.Bytes(), null, null)).Status.Should().Be(BankStatementIngestStatus.Ingested);
    }

    [Fact]
    public async Task ControlTotalMismatch_IsRejected_AndStoredForAudit()
    {
        var bad = new Bai2Builder { Groups = ThreeLines().Groups, GroupTotalDelta = 100 };

        var result = await Ingestion().IngestAsync(Tenant, bad.Bytes(), "bad.bai", null);

        result.Status.Should().Be(BankStatementIngestStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("98 group control total"));
        result.Transactions.Should().BeEmpty();
        var stored = await _store.GetFileAsync(Tenant, result.Statement.Id);
        stored!.Status.Should().Be(BankStatementStatus.Rejected);
        stored.BankFileKey.Should().BeNull();
        (await _store.GetTransactionsAsync(Tenant, result.Statement.Id)).Should().BeEmpty();

        // Re-sending the same bad bytes reports the same rejection.
        var again = await Ingestion().IngestAsync(Tenant, bad.Bytes(), "bad.bai", null);
        again.Status.Should().Be(BankStatementIngestStatus.Duplicate);
        again.IsAccepted.Should().BeFalse();
    }

    [Fact]
    public async Task Runner_DecidesEachLineOnce_AndPublishesEvents()
    {
        var ingest = await Ingestion().IngestAsync(Tenant, ThreeLines().Bytes(), null, null);
        var matcher = new CountingMatcher();

        var first = await Runner().RunAsync(ingest.Transactions, matcher);
        first.Matched.Should().Be(1);
        first.Suspense.Should().Be(1);
        first.Returns.Should().Be(1);
        _sink.Events.Should().HaveCount(3);
        _sink.Events.Should().OnlyContain(e => e.Type == BankReconciliationEvent.Decided);

        var again = await Ingestion().IngestAsync(Tenant, ThreeLines().Bytes(), null, null);
        var second = await Runner().RunAsync(again.Transactions, matcher);

        second.AlreadyDecided.Should().Be(3);
        matcher.Calls.Should().Be(3);
        _sink.Events.Should().HaveCount(3);
        (await _store.ListItemsAsync(Tenant, "test-service", ReconciliationStatus.Suspense, null, 10)).Should().ContainSingle();
    }

    [Fact]
    public async Task FailedEvents_AreRetriedOnTheNextRun()
    {
        var ingest = await Ingestion().IngestAsync(Tenant, ThreeLines().Bytes(), null, null);
        _sink.Fail = true;
        await Runner().RunAsync(ingest.Transactions, new CountingMatcher());
        _sink.Events.Should().BeEmpty();

        _sink.Fail = false;
        await Runner().RunAsync(ingest.Transactions, new CountingMatcher());

        _sink.Events.Should().HaveCount(3);
        (await _store.ListItemsAsync(Tenant, "test-service", null, null, 10)).Should().OnlyContain(i => i.EventPublishedAt != null);
    }

    [Fact]
    public async Task StalePendingClaim_IsTakenOver()
    {
        var ingest = await Ingestion().IngestAsync(Tenant, ThreeLines().Bytes(), null, null);
        var tx = ingest.Transactions[0];
        await _store.TryCreateItemAsync(new ReconciliationItem
        {
            Id = ReconciliationItem.IdFor("test-service", tx.Id), TenantId = Tenant, Service = "test-service",
            TransactionId = tx.Id, StatementId = tx.StatementId, Status = ReconciliationStatus.Pending, CreatedAt = _clock.Now.UtcDateTime,
        });

        var fresh = await Runner().RunAsync([tx], new CountingMatcher());
        fresh.InProgress.Should().Be(1);

        _clock.Now = _clock.Now.AddMinutes(30);
        var later = await Runner().RunAsync([tx], new CountingMatcher());
        later.Matched.Should().Be(1);
    }

    [Fact]
    public async Task OtherAccounts_TestGroups_AndCorrections_AreNotMatched()
    {
        var builder = new Bai2Builder
        {
            Groups =
            [
                new Group { Accounts = [new Account { Number = "11112222", Transactions = [new Tx("165", 1, CustomerReference: "A")] }] },
                new Group { Status = "4", Accounts = [new Account { Number = "99996655", Transactions = [new Tx("165", 2, CustomerReference: "B")] }] },
                new Group { Status = "3", Accounts = [new Account { Number = "99996655", Transactions = [new Tx("165", 3, CustomerReference: "C")] }] },
            ],
        };
        var ingest = await Ingestion().IngestAsync(Tenant, builder.Bytes(), null, null);
        var matcher = new CountingMatcher();

        var summary = await Runner(o => o.AccountSuffixes = ["6655"]).RunAsync(ingest.Transactions, matcher);

        summary.Ignored.Should().Be(2);
        summary.Suspense.Should().Be(1);
        matcher.Calls.Should().Be(0);
        summary.Items.Single(i => i.Status == ReconciliationStatus.Suspense).Reason.Should().Contain("correction");
    }

    [Fact]
    public async Task Resolve_ClosesAnOpenItem_Once()
    {
        var ingest = await Ingestion().IngestAsync(Tenant, ThreeLines().Bytes(), null, null);
        var summary = await Runner().RunAsync(ingest.Transactions, new CountingMatcher());
        var open = summary.Items.Single(i => i.Status == ReconciliationStatus.Suspense);

        var resolved = await Runner().ResolveAsync(Tenant, open.Id, dismiss: false, "Matched by hand to invoice INV-9", "finance-2", "PremiumInvoice", ["inv-9"]);

        resolved.Status.Should().Be(ReconciliationStatus.Resolved);
        resolved.MatchedEntityIds.Should().Equal("inv-9");
        _sink.Events.Should().Contain(e => e.Type == BankReconciliationEvent.ResolvedType && e.ItemId == open.Id);
        await FluentActions.Invoking(() => Runner().ResolveAsync(Tenant, open.Id, true, "again", "x"))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => Runner().ResolveAsync("other-tenant", open.Id, true, "x", "x"))
            .Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Invoking(() => Runner().ResolveAsync(Tenant, summary.Items.Single(i => i.Status == ReconciliationStatus.Return).Id, true, " ", "x"))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void ReferenceTokens_ComeFromReferencesAndText()
    {
        var tx = new BankTransaction { CustomerReference = "INV-2026-0042", BankReference = "BNK0001", Text = "ACME CO PREMIUM LBX7788 FOR MARCH" };
        BankReferenceTokens.From(tx).Should().Equal("INV-2026-0042", "BNK0001", "LBX7788");
    }
}
