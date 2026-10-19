using CloudHealthOffice.BankReconciliation;
using CloudHealthOffice.BankReconciliation.Testing;
using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using PremiumBillingService.BankReconciliation;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;
using Microsoft.AspNetCore.Mvc;
using static CloudHealthOffice.BankReconciliation.Testing.Bai2Builder;

namespace PremiumBillingService.Tests.BankReconciliation;

/// <summary>
/// BAI2 premium cash against the real Mongo repositories and the real cash
/// application: confirmed remittances post nothing, an exact single-invoice
/// match is applied once, everything weaker waits in suspense.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class PremiumBankReconciliationTests : IAsyncLifetime
{
    private const string Tenant = "tenant-bai2";
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _db = null!;
    private IPremiumInvoiceRepository _invoices = null!;
    private IRemittanceBatchRepository _batches = null!;
    private IRemittanceExceptionRepository _exceptions = null!;
    private CashApplicationService _cash = null!;
    private PremiumBankTransactionMatcher _matcher = null!;
    private readonly InMemoryBankReconciliationStore _store = new();
    private BankStatementIngestion _ingestion = null!;
    private BankReconciliationRunner _runner = null!;
    private readonly Mock<ICurrentActor> _actor = new();
    private BankReconciliationOptions _reconOptions = null!;

    public PremiumBankReconciliationTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _db = _mongo.CreateDatabase("pb_bai2");
        var http = new RequestContext { HttpContext = new DefaultHttpContext() };
        http.HttpContext.Items["TenantId"] = Tenant;
        _invoices = new PremiumInvoiceRepositoryMongo(_db, http, NullLogger<PremiumInvoiceRepositoryMongo>.Instance);
        _batches = new RemittanceBatchRepositoryMongo(_db, http);
        _exceptions = new RemittanceExceptionRepositoryMongo(_db, http);
        var accounts = new SponsorAccountRepositoryMongo(_db, http);

        var actor = _actor;
        actor.SetupGet(a => a.UserId).Returns("finance-user-1");
        actor.SetupGet(a => a.TenantId).Returns(Tenant);
        actor.SetupGet(a => a.IsAuthenticated).Returns(true);
        _cash = new CashApplicationService(_batches, _exceptions, _invoices, accounts, actor.Object, NullLogger<CashApplicationService>.Instance);
        _matcher = new PremiumBankTransactionMatcher(_cash, _batches, _invoices,
            Options.Create(new PremiumBankReconciliationOptions()), NullLogger<PremiumBankTransactionMatcher>.Instance,
            new BankStatementFreeze(_store));

        _reconOptions = new BankReconciliationOptions { ServiceName = "premium-billing-service" };
        var options = Options.Create(_reconOptions);
        _ingestion = new BankStatementIngestion(_store, new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), "t"),
            options, NullLogger<BankStatementIngestion>.Instance);
        _runner = new BankReconciliationRunner(_store, new LoggingBankReconciliationEventSink(NullLogger<LoggingBankReconciliationEventSink>.Instance),
            options, NullLogger<BankReconciliationRunner>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private sealed class RequestContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private async Task<PremiumInvoice> Invoice(string number, decimal amount)
    {
        var invoice = new PremiumInvoice
        {
            InvoiceNumber = number,
            GroupNumber = "GRP-BAI",
            SponsorName = "Synthetic Employer",
            BillingPeriodStart = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            BillingPeriodEnd = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc),
            Status = InvoiceStatus.Sent,
            LineItems = { new InvoiceLineItem { MemberId = "MBR-1", TotalPremium = amount } },
        };
        invoice.RecalculateTotals();
        return await _invoices.CreateAsync(invoice);
    }

    private async Task<BankReconciliationSummary> Reconcile(Bai2Builder file)
    {
        var ingest = await _ingestion.IngestAsync(Tenant, file.Bytes(), "stmt.bai", "finance-user-1");
        ingest.IsAccepted.Should().BeTrue();
        return await _runner.RunAsync(ingest.Transactions, _matcher);
    }

    private Task<PremiumInvoice?> Reload(PremiumInvoice invoice) => _invoices.GetByIdAsync(invoice.Id);

    private static readonly DateTime AsOf = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CreditConfirmingARecorded820_IsMatched_AndPostsNothing()
    {
        var invoice = await Invoice("INV-2026-1001", 1000m);
        await _cash.ApplyAsync(new RemittanceAdvice
        {
            Source = RemittanceSource.X12820, TransactionHandlingCode = "C", CreditDebitFlag = "C", PaymentMethod = "ACH",
            TraceNumber = "TRN8201001", PayerId = "SPONSOR1", PaymentAmount = 1000m, PaymentDate = AsOf,
            Items = [new RemittanceItem { LineNumber = 1, ReferenceQualifier = "IK", Reference = "INV-2026-1001", Amount = 1000m }],
        });

        var summary = await Reconcile(Single(new Tx("165", 100000, CustomerReference: "TRN8201001", Text: "SPONSOR1 PREMIUM")));

        var item = summary.Items.Single();
        item.Status.Should().Be(ReconciliationStatus.Matched);
        item.MatchedEntityType.Should().Be("RemittanceBatch");
        (await Reload(invoice))!.Payments.Should().ContainSingle();
    }

    [Fact]
    public async Task ExactSingleInvoiceMatch_IsAppliedOnce_EvenWhenTheFileIsSentAgain()
    {
        var invoice = await Invoice("INV-2026-2001", 1250.75m);
        var file = Single(new Tx("169", 125075, BankReference: "BNK77", CustomerReference: "ACH55501", Text: "PREMIUM INV-2026-2001"));

        var first = await Reconcile(file);

        first.Items.Single().Status.Should().Be(ReconciliationStatus.Applied);
        var paid = (await Reload(invoice))!;
        paid.Status.Should().Be(InvoiceStatus.Paid);
        paid.BalanceDue.Should().Be(0);
        var batch = await _batches.GetByIdAsync(first.Items.Single().MatchedEntityIds.Single());
        batch!.Source.Should().Be(RemittanceSource.BankStatement);

        var second = await Reconcile(file);
        second.AlreadyDecided.Should().Be(1);
        (await Reload(invoice))!.Payments.Should().ContainSingle();
    }

    [Fact]
    public async Task AnInvoiceArrivingLaterBy820_IsHeldAsAPossibleDuplicate()
    {
        await Invoice("INV-2026-2101", 400m);
        await Reconcile(Single(new Tx("165", 40000, CustomerReference: "TRN9990001", Text: "INV-2026-2101")));

        var late820 = await _cash.ApplyAsync(new RemittanceAdvice
        {
            Source = RemittanceSource.X12820, TransactionHandlingCode = "C", CreditDebitFlag = "C", PaymentMethod = "ACH",
            TraceNumber = "TRN9990001", PayerId = "SPONSOR2", PaymentAmount = 400m, PaymentDate = AsOf,
            Items = [new RemittanceItem { LineNumber = 1, ReferenceQualifier = "IK", Reference = "INV-2026-2101", Amount = 400m }],
        });

        late820.AppliedAmount.Should().Be(0);
        (await _exceptions.ListAsync()).Should().Contain(e => e.Reason == RemittanceExceptionReason.PossibleDuplicate);
    }

    [Fact]
    public async Task AmountDifferentFromTheBalance_WaitsInSuspense_AndIsNotApplied()
    {
        var invoice = await Invoice("INV-2026-3001", 500m);

        var item = (await Reconcile(Single(new Tx("165", 45000, CustomerReference: "INV-2026-3001")))).Items.Single();

        item.Status.Should().Be(ReconciliationStatus.Suspense);
        item.Reason.Should().Contain("500.00").And.Contain("450.00");
        item.Candidates.Should().ContainSingle(c => c.Contains(invoice.Id));
        (await Reload(invoice))!.Payments.Should().BeEmpty();
    }

    [Fact]
    public async Task AmountOnlyMatch_WithoutAReference_IsNeverApplied()
    {
        var invoice = await Invoice("INV-2026-3101", 75m);

        var item = (await Reconcile(Single(new Tx("165", 7500)))).Items.Single();

        item.Status.Should().Be(ReconciliationStatus.Suspense);
        item.Reason.Should().Contain("no reference");
        (await Reload(invoice))!.Payments.Should().BeEmpty();
    }

    [Fact]
    public async Task SeveralInvoicesNamed_WaitInSuspense()
    {
        await Invoice("INV-2026-4001", 100m);
        await Invoice("INV-2026-4002", 100m);

        var item = (await Reconcile(Single(new Tx("165", 10000, CustomerReference: "INV-2026-4001", Text: "AND INV-2026-4002")))).Items.Single();

        item.Status.Should().Be(ReconciliationStatus.Suspense);
        item.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task LockboxDeposit_MatchesTheDaysLockboxBatches()
    {
        await Invoice("INV-2026-5001", 300m);
        await Invoice("INV-2026-5002", 200m);
        foreach (var (check, amount, invoice) in new[] { ("1001", 300m, "INV-2026-5001"), ("1002", 200m, "INV-2026-5002") })
            await _cash.ApplyAsync(new RemittanceAdvice
            {
                Source = RemittanceSource.Lockbox, PaymentMethod = "CHK", CheckNumber = check,
                TraceNumber = $"LBX-7788-20261009-1-{check}-{check}", PayerId = "PAYER", PaymentAmount = amount, PaymentDate = AsOf,
                Items = [new RemittanceItem { LineNumber = 1, Reference = invoice, Amount = amount }],
            });

        var summary = await Reconcile(Single(
            new Tx("115", 50000, CustomerReference: "7788", Text: "LOCKBOX DEPOSIT"),
            new Tx("115", 60000, CustomerReference: "7788", Text: "LOCKBOX DEPOSIT")));

        summary.Items[0].Status.Should().Be(ReconciliationStatus.Matched);
        summary.Items[0].MatchedEntityIds.Should().HaveCount(2);
        summary.Items[1].Status.Should().Be(ReconciliationStatus.Suspense);
        summary.Items[1].Reason.Should().Contain("total 500.00");
    }

    [Fact]
    public async Task LockboxPaymentDateLookup_IncludesLateImports_AndFiltersTenantSourceStatusAndDay()
    {
        var batches = _db.GetCollection<RemittanceBatch>("RemittanceBatches");
        RemittanceBatch Batch(string id, string tenant = Tenant, int dayOffset = 0,
            RemittanceSource source = RemittanceSource.Lockbox,
            RemittanceBatchStatus status = RemittanceBatchStatus.Completed) => new()
        {
            Id = id, TenantId = tenant, Source = source, Status = status,
            PaymentDate = AsOf.AddDays(dayOffset), ReceivedAt = AsOf.AddMonths(6),
            PaymentAmount = 500m, TraceNumber = "LBX-7788-" + id,
        };
        await batches.InsertManyAsync([
            Batch("late"), Batch("foreign", tenant: "another-tenant"),
            Batch("previous", dayOffset: -1), Batch("next", dayOffset: 1),
            Batch("820", source: RemittanceSource.X12820),
            Batch("not-posted", status: RemittanceBatchStatus.NotPosted),
        ]);

        // An unspecified BAI2 business date must have the same UTC boundaries
        // on Phoenix and UTC hosts; the receipt date is deliberately far later.
        var matches = (await _batches.FindLockboxByPaymentDateAsync(
            DateTime.SpecifyKind(AsOf, DateTimeKind.Unspecified))).ToList();
        matches.Should().ContainSingle().Which.Id.Should().Be("late");
        var item = (await Reconcile(Single(
            new Tx("115", 50000, CustomerReference: "7788", Text: "LOCKBOX DEPOSIT")))).Items.Single();
        item.Status.Should().Be(ReconciliationStatus.Matched);
        item.MatchedEntityIds.Should().ContainSingle().Which.Should().Be("late");
    }

    [Fact]
    public async Task AchReturn_IsFlagged_AndNotApplied()
    {
        var item = (await Reconcile(Single(new Tx("555", 10000, CustomerReference: "INV-2026-6001", Text: "RETURN R01")))).Items.Single();
        item.Status.Should().Be(ReconciliationStatus.Return);
        item.Kind.Should().Be("AchReturn");
    }

    [Fact]
    public async Task NonCashLines_AreIgnored()
    {
        (await Reconcile(Single(new Tx("475", 10000, CustomerReference: "1001")))).Items.Single().Status.Should().Be(ReconciliationStatus.Ignored);
    }

    [Fact]
    public async Task SuspenseItem_AppliedByAPerson_PostsThroughCashApplication()
    {
        var invoice = await Invoice("INV-2026-7001", 500m);
        var summary = await Reconcile(Single(new Tx("165", 30000, CustomerReference: "INV-2026-7001")));
        var item = summary.Items.Single();
        var tx = (await _store.GetTransactionsAsync(Tenant, item.StatementId)).Single();

        var decision = await _matcher.ApplyAsync(tx, invoice);
        var resolved = await _runner.ResolveAsync(Tenant, item.Id, false, "Partial payment confirmed with the sponsor", "finance-user-2",
            decision.MatchedEntityType, decision.MatchedEntityIds);

        resolved.Status.Should().Be(ReconciliationStatus.Resolved);
        var reloaded = (await Reload(invoice))!;
        reloaded.Status.Should().Be(InvoiceStatus.PartiallyPaid);
        reloaded.BalanceDue.Should().Be(200m);

        // Applying the same line again does not post twice.
        (await _matcher.ApplyAsync(tx, invoice)).Status.Should().Be(ReconciliationStatus.Applied);
        (await Reload(invoice))!.Payments.Should().ContainSingle();
    }

    // ── review follow-ups: the same money by 820 and bank ─────────────

    private Task<RemittanceBatch> Apply820(string trace, decimal amount, DateTime date, string invoice) =>
        _cash.ApplyAsync(new RemittanceAdvice
        {
            Source = RemittanceSource.X12820, TransactionHandlingCode = "C", CreditDebitFlag = "C", PaymentMethod = "ACH",
            TraceNumber = trace, PayerId = "SPONSOR9", PaymentAmount = amount, PaymentDate = date,
            Items = [new RemittanceItem { LineNumber = 1, ReferenceQualifier = "IK", Reference = invoice, Amount = amount }],
        });

    private static Bai2Builder Line(Tx tx, string asOf = "261009") => new()
    {
        Groups = [new Group { AsOfDate = asOf, Accounts = [new Account { Transactions = [tx] }] }],
    };

    [Fact]
    public async Task Partial820First_BankLineWithAnotherReferenceFormat_IsAPossibleDuplicate_NotApplied()
    {
        var invoice = await Invoice("INV-2026-8001", 1000m);
        await Apply820("TRN8001", 500m, AsOf, "INV-2026-8001");

        // The bank reports the ACH trace as a 15-digit number: the recorded-payment key misses.
        var item = (await Reconcile(Line(new Tx("165", 50000, CustomerReference: "000000000008001", Text: "PREMIUM INV-2026-8001")))).Items.Single();

        item.Status.Should().Be(ReconciliationStatus.Suspense);
        item.Reason.Should().StartWith("PossibleDuplicate");
        var reloaded = (await Reload(invoice))!;
        reloaded.Payments.Should().ContainSingle();
        reloaded.BalanceDue.Should().Be(500m);
    }

    [Fact]
    public async Task Partial820OnFriday_BankLineOnMonday_IsAPossibleDuplicate_NotApplied()
    {
        var invoice = await Invoice("INV-2026-8002", 1000m);
        await Apply820("TRN8002", 500m, AsOf, "INV-2026-8002"); // Friday 2026-10-09

        var item = (await Reconcile(Line(new Tx("165", 50000, CustomerReference: "TRN8002", Text: "INV-2026-8002"), asOf: "261012"))).Items.Single();

        item.Status.Should().Be(ReconciliationStatus.Suspense);
        item.Reason.Should().StartWith("PossibleDuplicate");
        (await Reload(invoice))!.Payments.Should().ContainSingle();
    }

    [Fact]
    public async Task BankFirst_820WithAnotherReferenceFormat_IsHeldAsAPossibleDuplicate()
    {
        var invoice = await Invoice("INV-2026-8003", 500m);
        (await Reconcile(Line(new Tx("165", 50000, CustomerReference: "000000000008003", Text: "INV-2026-8003")))).Items.Single()
            .Status.Should().Be(ReconciliationStatus.Applied);

        var late = await Apply820("TRN8003", 500m, AsOf, "INV-2026-8003");

        late.AppliedAmount.Should().Be(0);
        (await _exceptions.ListAsync()).Should().Contain(e => e.Reason == RemittanceExceptionReason.PossibleDuplicate && e.TraceNumber == "TRN8003");
        var reloaded = (await Reload(invoice))!;
        reloaded.Payments.Should().ContainSingle();
        reloaded.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task BankFirstOnMonday_820DatedFriday_IsHeldAsAPossibleDuplicate()
    {
        var invoice = await Invoice("INV-2026-8004", 500m);
        (await Reconcile(Line(new Tx("165", 50000, CustomerReference: "BANKREF8004", Text: "INV-2026-8004"), asOf: "261012"))).Items.Single()
            .Status.Should().Be(ReconciliationStatus.Applied);

        var late = await Apply820("TRN8004", 500m, AsOf, "INV-2026-8004");

        late.AppliedAmount.Should().Be(0);
        (await Reload(invoice))!.Payments.Should().ContainSingle();
    }

    [Fact]
    public async Task OverpaidBankLine_ThenAn820ForTheSameMoney_IsHeldAsAPossibleDuplicate()
    {
        var invoice = await Invoice("INV-2026-8007", 500m);
        var item = (await Reconcile(Line(new Tx("165", 60000, CustomerReference: "ACH8007", Text: "INV-2026-8007")))).Items.Single();
        item.Status.Should().Be(ReconciliationStatus.Suspense);

        // A person applies the 600.00 line: 500.00 pays the invoice, 100.00 becomes unapplied credit.
        var tx = (await _store.GetTransactionsAsync(Tenant, item.StatementId)).Single();
        (await _matcher.ApplyAsync(tx, invoice)).Status.Should().Be(ReconciliationStatus.Applied);

        // The sponsor's 820 for the same 600.00 arrives under its own trace.
        var late = await Apply820("TRN8007", 600m, AsOf, "INV-2026-8007");

        late.AppliedAmount.Should().Be(0);
        late.UnappliedCreditAmount.Should().Be(0);
        (await _exceptions.ListAsync()).Should().Contain(e => e.Reason == RemittanceExceptionReason.PossibleDuplicate && e.TraceNumber == "TRN8007");
        (await Reload(invoice))!.Payments.Should().ContainSingle();
    }

    [Fact]
    public async Task FrozenStatement_PostsNoCash_AtTheCommitPoint()
    {
        var invoice = await Invoice("INV-2026-8009", 250m);
        var ingest = await _ingestion.IngestAsync(Tenant, Line(new Tx("165", 25000, CustomerReference: "INV-2026-8009")).Bytes(), null, null);
        await new BankStatementSupersessionService(_store, Options.Create(_reconOptions), NullLogger<BankStatementSupersessionService>.Instance)
            .RequestAsync(Tenant, ingest.Statement.Id, "corrected file arriving", "alice");

        await FluentActions.Invoking(() => _matcher.ApplyAsync(ingest.Transactions.Single(), invoice))
            .Should().ThrowAsync<StatementFrozenException>();
        (await Reload(invoice))!.Payments.Should().BeEmpty();
    }

    [Fact]
    public async Task DraftInvoice_IsNeverMatchedOrApplied()
    {
        var invoice = await Invoice("INV-2026-8008", 300m);
        invoice.Status = InvoiceStatus.Draft;
        await _invoices.UpdateAsync(invoice);

        var item = (await Reconcile(Line(new Tx("165", 30000, CustomerReference: "INV-2026-8008")))).Items.Single();

        item.Status.Should().Be(ReconciliationStatus.Suspense);
        item.Candidates.Should().BeEmpty();
        (await Reload(invoice))!.Payments.Should().BeEmpty();

        var controller = new PremiumBankStatementsController(_ingestion, _runner, _store, _matcher, _invoices, _batches, _actor.Object,
            Options.Create(_reconOptions), new BankStatementSupersessionService(_store, Options.Create(_reconOptions), NullLogger<BankStatementSupersessionService>.Instance));
        (await controller.Resolve(item.Id, new ResolvePremiumSuspenseRequest { Action = PremiumSuspenseAction.ApplyToInvoice, InvoiceId = invoice.Id, Note = "try" }, CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await Reload(invoice))!.Payments.Should().BeEmpty();
    }

    [Fact]
    public async Task LineAppliedBeforeACrash_IsRecordedAsApplied_WhenTheFileIsReconciledAgain()
    {
        var invoice = await Invoice("INV-2026-8005", 600m);
        var file = Line(new Tx("165", 60000, CustomerReference: "ACH8005", Text: "INV-2026-8005"));
        var ingest = await _ingestion.IngestAsync(Tenant, file.Bytes(), null, null);
        // The run applied the line and stopped before recording its decision.
        await _matcher.ApplyAsync(ingest.Transactions.Single(), invoice);

        var item = (await _runner.RunAsync(ingest.Transactions, _matcher)).Items.Single();

        item.Status.Should().Be(ReconciliationStatus.Applied);
        (await Reload(invoice))!.Payments.Should().ContainSingle();
    }

    [Fact]
    public async Task OnlyInvoiceShapedTokens_AreLookedUp()
    {
        var invoice = await Invoice("ABC12345", 80m);

        var item = (await Reconcile(Line(new Tx("165", 8000, CustomerReference: "ABC12345")))).Items.Single();

        item.Status.Should().Be(ReconciliationStatus.Suspense);
        (await Reload(invoice))!.Payments.Should().BeEmpty();
    }

    [Fact]
    public async Task LockboxDepositSuspense_CannotBeAppliedToAnInvoice()
    {
        var invoice = await Invoice("INV-2026-8006", 500m);
        var item = (await Reconcile(Line(new Tx("115", 50000, CustomerReference: "7788")))).Items.Single();
        item.Kind.Should().Be("LockboxDeposit");
        item.Status.Should().Be(ReconciliationStatus.Suspense);

        var controller = new PremiumBankStatementsController(_ingestion, _runner, _store, _matcher, _invoices, _batches, _actor.Object,
            Options.Create(_reconOptions), new BankStatementSupersessionService(_store, Options.Create(_reconOptions), NullLogger<BankStatementSupersessionService>.Instance));

        var result = await controller.Resolve(item.Id, new ResolvePremiumSuspenseRequest
        {
            Action = PremiumSuspenseAction.ApplyToInvoice, InvoiceId = invoice.Id, Note = "try",
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await Reload(invoice))!.Payments.Should().BeEmpty();
    }
}
