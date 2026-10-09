using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using PremiumBillingService.Edi;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Remittance;

/// <summary>
/// 820 and lockbox cash application against a real mongod and the real Mongo
/// repositories: every assertion on a balance re-reads what was saved.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class CashApplicationTests : IAsyncLifetime
{
    private const string Tenant = "tenant-cash";
    private const string Group = "GRP001";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private IPremiumInvoiceRepository _invoices = null!;
    private IRemittanceBatchRepository _batches = null!;
    private IRemittanceExceptionRepository _exceptions = null!;
    private ISponsorAccountRepository _accounts = null!;
    private CashApplicationService _service = null!;

    public CashApplicationTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("pb_cash_application");
        var http = new RequestContext { HttpContext = new DefaultHttpContext() };
        http.HttpContext.Items["TenantId"] = Tenant;

        _invoices = new PremiumInvoiceRepositoryMongo(_database, http, NullLogger<PremiumInvoiceRepositoryMongo>.Instance);
        _batches = new RemittanceBatchRepositoryMongo(_database, http);
        _exceptions = new RemittanceExceptionRepositoryMongo(_database, http);
        _accounts = new SponsorAccountRepositoryMongo(_database, http);

        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.UserId).Returns("finance-user-1");
        actor.SetupGet(a => a.TenantId).Returns(Tenant);
        actor.SetupGet(a => a.IsAuthenticated).Returns(true);
        _service = new CashApplicationService(_batches, _exceptions, _invoices, _accounts, actor.Object,
            NullLogger<CashApplicationService>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    /// <summary>A fixed request (not AsyncLocal), as the repositories read the tenant from it.</summary>
    private sealed class RequestContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private async Task<PremiumInvoice> Invoice(string number, decimal amount, InvoiceStatus status = InvoiceStatus.Sent, string group = Group)
    {
        var invoice = new PremiumInvoice
        {
            InvoiceNumber = number,
            GroupNumber = group,
            SponsorName = "Synthetic Employer",
            BillingPeriodStart = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            BillingPeriodEnd = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            Status = status,
            LineItems = { new InvoiceLineItem { MemberId = "MBR-1", TotalPremium = amount } }
        };
        invoice.RecalculateTotals();
        return await _invoices.CreateAsync(invoice);
    }

    private async Task<RemittanceBatch> Apply820(Synthetic820.Transaction transaction) =>
        await _service.ApplyAsync(Edi820Parser.Parse(Synthetic820.Single(transaction)).Single());

    private async Task<PremiumInvoice> Reload(PremiumInvoice invoice) => (await _invoices.GetByIdAsync(invoice.Id))!;

    // ── the four required cases ───────────────────────────────────────

    [Fact]
    public async Task ExactPayment_PaysTheInvoice_AndTheAccountOwesNothing()
    {
        var invoice = await Invoice("INV-GRP001-2026-03", 1200.00m);

        var batch = await Apply820(new Synthetic820.Transaction { Amount = 1200.00m, Trace = "EXACT-1" }
            .Organization().Rmr("INV-GRP001-2026-03", 1200.00m, 1200.00m));

        var app = batch.Applications.Should().ContainSingle().Subject;
        app.Outcome.Should().Be(CashApplicationOutcome.ExactMatch);
        app.AppliedAmount.Should().Be(1200.00m);
        app.UnappliedCreditAmount.Should().Be(0m);
        batch.Status.Should().Be(RemittanceBatchStatus.Completed);
        batch.AppliedAmount.Should().Be(1200.00m);

        var saved = await Reload(invoice);
        saved.Status.Should().Be(InvoiceStatus.Paid);
        saved.TotalPaid.Should().Be(1200.00m);
        saved.BalanceDue.Should().Be(0m);
        var payment = saved.Payments.Should().ContainSingle().Subject;
        payment.ReferenceNumber.Should().Be("EXACT-1");
        payment.RemittanceBatchId.Should().Be(batch.Id);
        payment.RemittanceLine.Should().Be(1);
        payment.RecordedBy.Should().Be("finance-user-1");

        var account = (await _accounts.GetAsync(Group))!;
        account.OpenInvoiceBalance.Should().Be(0m);
        account.UnappliedCredit.Should().Be(0m);
        account.NetBalance.Should().Be(0m);
        account.LastPaymentAt.Should().Be(new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task PartialPayment_LeavesTheBalance_OnTheInvoiceAndTheAccount()
    {
        var invoice = await Invoice("INV-GRP001-2026-03", 1200.00m);
        await Invoice("INV-GRP001-2026-02", 300.00m); // another open invoice of the group

        var batch = await Apply820(new Synthetic820.Transaction { Amount = 800.00m, Trace = "PARTIAL-1" }
            .Organization().Rmr("INV-GRP001-2026-03", 800.00m, 1200.00m));

        batch.Applications.Single().Outcome.Should().Be(CashApplicationOutcome.PartialPayment);
        var saved = await Reload(invoice);
        saved.Status.Should().Be(InvoiceStatus.PartiallyPaid);
        saved.TotalPaid.Should().Be(800.00m);
        saved.BalanceDue.Should().Be(400.00m);

        var account = (await _accounts.GetAsync(Group))!;
        account.OpenInvoiceBalance.Should().Be(700.00m); // 400 + 300
        account.NetBalance.Should().Be(700.00m);
    }

    [Fact]
    public async Task Overpayment_PaysTheBalance_AndHoldsTheRestAsUnappliedCredit()
    {
        var invoice = await Invoice("INV-GRP001-2026-03", 1200.00m);

        var batch = await Apply820(new Synthetic820.Transaction { Amount = 1500.00m, Trace = "OVER-1" }
            .Organization().Rmr("INV-GRP001-2026-03", 1500.00m, 1200.00m));

        var app = batch.Applications.Single();
        app.Outcome.Should().Be(CashApplicationOutcome.Overpayment);
        app.AppliedAmount.Should().Be(1200.00m);
        app.UnappliedCreditAmount.Should().Be(300.00m);
        batch.UnappliedCreditAmount.Should().Be(300.00m);

        var saved = await Reload(invoice);
        saved.Status.Should().Be(InvoiceStatus.Paid);
        saved.TotalPaid.Should().Be(1200.00m);
        saved.BalanceDue.Should().Be(0m); // never negative

        var account = (await _accounts.GetAsync(Group))!;
        account.UnappliedCredit.Should().Be(300.00m);
        account.OpenInvoiceBalance.Should().Be(0m);
        account.NetBalance.Should().Be(-300.00m);
        var entry = account.Entries.Should().ContainSingle().Subject;
        entry.Type.Should().Be(SponsorAccountEntryType.OverpaymentCredit);
        entry.Amount.Should().Be(300.00m);
        entry.InvoiceId.Should().Be(invoice.Id);
        entry.TraceNumber.Should().Be("OVER-1");
    }

    [Fact]
    public async Task UnmatchedReference_GoesToTheExceptionsQueue_AndPostsNothing()
    {
        var invoice = await Invoice("INV-GRP001-2026-03", 1200.00m);

        var batch = await Apply820(new Synthetic820.Transaction { Amount = 1700.00m, Trace = "MIXED-1" }
            .Organization()
            .Rmr("INV-GRP001-2026-03", 1200.00m)
            .Rmr("INV-NOPE-2026-03", 500.00m));

        batch.Applications.Select(a => a.Outcome).Should().Equal(CashApplicationOutcome.ExactMatch, CashApplicationOutcome.Exception);
        batch.ExceptionCount.Should().Be(1);
        batch.ExceptionAmount.Should().Be(500.00m);
        batch.AppliedAmount.Should().Be(1200.00m);

        var queued = (await _exceptions.ListAsync(RemittanceExceptionStatus.Open)).Should().ContainSingle().Subject;
        queued.Reason.Should().Be(RemittanceExceptionReason.InvoiceNotFound);
        queued.Reference.Should().Be("INV-NOPE-2026-03");
        queued.Amount.Should().Be(500.00m);
        queued.TraceNumber.Should().Be("MIXED-1");
        queued.BatchId.Should().Be(batch.Id);
        queued.LineNumber.Should().Be(2);
        batch.Applications[1].ExceptionId.Should().Be(queued.Id);

        (await Reload(invoice)).Status.Should().Be(InvoiceStatus.Paid);
    }

    // ── other rules ───────────────────────────────────────────────────

    [Fact]
    public async Task SamePaymentTwice_IsRefused_AndNotPostedAgain()
    {
        var invoice = await Invoice("INV-GRP001-2026-03", 1200.00m);
        var transaction = new Synthetic820.Transaction { Amount = 600.00m, Trace = "DUP-1" }.Organization().Rmr("INV-GRP001-2026-03", 600.00m);
        var first = await Apply820(transaction);

        var again = () => Apply820(transaction);

        (await again.Should().ThrowAsync<DuplicateRemittanceException>()).Which.BatchId.Should().Be(first.Id);
        var saved = await Reload(invoice);
        saved.TotalPaid.Should().Be(600.00m);
        saved.Payments.Should().ContainSingle();
    }

    [Fact]
    public async Task TwoItemsForOneInvoice_AreAppliedInOrder()
    {
        var invoice = await Invoice("INV-GRP001-2026-03", 1000.00m);

        var batch = await Apply820(new Synthetic820.Transaction { Amount = 1100.00m, Trace = "INDIV-1" }
            .Individual("1", "EMP-1", "MBR-1", "SAMPLE", "ALEX").Rmr("INV-GRP001-2026-03", 600.00m, qualifier: "AZ")
            .Individual("2", "EMP-2", "MBR-2", "TEST", "JORDAN").Rmr("INV-GRP001-2026-03", 500.00m, qualifier: "AZ"));

        batch.Applications.Select(a => (a.Outcome, a.AppliedAmount, a.UnappliedCreditAmount, a.MemberId)).Should().Equal(
            (CashApplicationOutcome.PartialPayment, 600.00m, 0m, "MBR-1"),
            (CashApplicationOutcome.Overpayment, 400.00m, 100.00m, "MBR-2"));
        var saved = await Reload(invoice);
        saved.Payments.Select(p => p.MemberId).Should().Equal("MBR-1", "MBR-2");
        saved.BalanceDue.Should().Be(0m);
        (await _accounts.GetAsync(Group))!.UnappliedCredit.Should().Be(100.00m);
    }

    [Fact]
    public async Task VoidedInvoice_IsAnException()
    {
        await Invoice("INV-GRP001-2026-01", 500.00m, InvoiceStatus.Voided);

        var batch = await Apply820(new Synthetic820.Transaction { Amount = 500.00m, Trace = "VOID-1" }
            .Organization().Rmr("INV-GRP001-2026-01", 500.00m));

        batch.Applications.Single().Outcome.Should().Be(CashApplicationOutcome.Exception);
        (await _exceptions.ListAsync()).Single().Reason.Should().Be(RemittanceExceptionReason.InvoiceClosed);
    }

    [Fact]
    public async Task MoneyWithoutDetail_IsAnUnallocatedRemainder()
    {
        await Invoice("INV-GRP001-2026-03", 1000.00m);

        var batch = await Apply820(new Synthetic820.Transaction { Amount = 1250.00m, Trace = "REM-1" }
            .Organization().Rmr("INV-GRP001-2026-03", 1000.00m));

        var queued = (await _exceptions.ListAsync()).Single();
        queued.Reason.Should().Be(RemittanceExceptionReason.UnallocatedRemainder);
        queued.Amount.Should().Be(250.00m);
        queued.LineNumber.Should().Be(0);
        (batch.AppliedAmount + batch.UnappliedCreditAmount + batch.ExceptionAmount).Should().Be(batch.PaymentAmount);
    }

    [Fact]
    public async Task DetailExceedingThePayment_PostsNothing()
    {
        var invoice = await Invoice("INV-GRP001-2026-03", 1000.00m);

        var batch = await Apply820(new Synthetic820.Transaction { Amount = 900.00m, Trace = "OOB-1" }
            .Organization().Rmr("INV-GRP001-2026-03", 1000.00m));

        batch.AppliedAmount.Should().Be(0m);
        batch.Applications.Single().Outcome.Should().Be(CashApplicationOutcome.Exception);
        var queued = (await _exceptions.ListAsync()).Single();
        queued.Reason.Should().Be(RemittanceExceptionReason.DetailExceedsPayment);
        queued.Amount.Should().Be(900.00m);
        (await Reload(invoice)).TotalPaid.Should().Be(0m);
    }

    [Fact]
    public async Task RemittanceOnly820_PostsNothing()
    {
        var invoice = await Invoice("INV-GRP001-2026-03", 1000.00m);

        var batch = await Apply820(new Synthetic820.Transaction { HandlingCode = "I", Amount = 1000.00m, Trace = "INFO-1" }
            .Organization().Rmr("INV-GRP001-2026-03", 1000.00m));

        batch.Status.Should().Be(RemittanceBatchStatus.NotPosted);
        batch.Warnings.Should().Contain(w => w.Contains("moves no money"));
        (await Reload(invoice)).TotalPaid.Should().Be(0m);
    }

    [Fact]
    public async Task Lockbox_GoesThroughTheSameApplication()
    {
        var march = await Invoice("INV-GRP001-2026-03", 1000.00m);
        var feb = await Invoice("INV-GRP001-2026-02", 400.00m);
        const string csv = """
            batch,item,deposit_date,check_number,payer_id,payer_name,check_amount,invoice_number,amount
            007,1,2026-03-06,20001,EMPLOYER-A,Synthetic Employer,1450.00,INV-GRP001-2026-03,1000.00
            007,1,2026-03-06,20001,EMPLOYER-A,Synthetic Employer,1450.00,INV-GRP001-2026-02,450.00
            """;

        var batch = await _service.ApplyAsync(LockboxCsvParser.Parse(csv).Single());

        batch.Source.Should().Be(RemittanceSource.Lockbox);
        batch.CheckNumber.Should().Be("20001");
        batch.Applications.Select(a => a.Outcome).Should().Equal(CashApplicationOutcome.ExactMatch, CashApplicationOutcome.Overpayment);
        (await Reload(march)).Status.Should().Be(InvoiceStatus.Paid);
        (await Reload(feb)).Status.Should().Be(InvoiceStatus.Paid);
        var account = (await _accounts.GetAsync(Group))!;
        account.UnappliedCredit.Should().Be(50.00m);
        account.NetBalance.Should().Be(-50.00m);
    }

    // ── exceptions queue ──────────────────────────────────────────────

    [Fact]
    public async Task Exception_AppliedToAnInvoice_UpdatesTheInvoiceAndAccount()
    {
        var invoice = await Invoice("INV-GRP001-2026-03", 1000.00m);
        await Apply820(new Synthetic820.Transaction { Amount = 1000.00m, Trace = "TYPO-1" }
            .Organization().Rmr("INV-GRP001-2026-3", 1000.00m)); // payer's typo
        var queued = (await _exceptions.ListAsync()).Single();

        var resolved = await _service.ResolveExceptionAsync(queued.Id, new ResolveRemittanceExceptionRequest
        {
            Action = RemittanceExceptionAction.ApplyToInvoice,
            InvoiceId = invoice.Id,
            Note = "Payer typo in RMR02; confirmed by phone"
        });

        resolved.Status.Should().Be(RemittanceExceptionStatus.Applied);
        resolved.ResolvedBy.Should().Be("finance-user-1");
        (await _exceptions.ListAsync(RemittanceExceptionStatus.Open)).Should().BeEmpty();
        var saved = await Reload(invoice);
        saved.Status.Should().Be(InvoiceStatus.Paid);
        saved.Payments.Single().ReferenceNumber.Should().Be("TYPO-1");
        (await _accounts.GetAsync(Group))!.OpenInvoiceBalance.Should().Be(0m);
    }

    [Fact]
    public async Task Exception_CreditedToASponsor_RaisesItsUnappliedCredit()
    {
        await Invoice("INV-GRP001-2026-03", 1000.00m);
        await Apply820(new Synthetic820.Transaction { Amount = 250.00m, Trace = "CR-1" }.Organization().Rmr("UNKNOWN", 250.00m));
        var queued = (await _exceptions.ListAsync()).Single();

        await _service.ResolveExceptionAsync(queued.Id, new ResolveRemittanceExceptionRequest
        {
            Action = RemittanceExceptionAction.CreditSponsorAccount,
            GroupNumber = Group,
            Note = "Advance premium for April"
        });

        var account = (await _accounts.GetAsync(Group))!;
        account.UnappliedCredit.Should().Be(250.00m);
        account.OpenInvoiceBalance.Should().Be(1000.00m);
        account.NetBalance.Should().Be(750.00m);
        account.Entries.Single().Type.Should().Be(SponsorAccountEntryType.ExceptionCredit);
    }

    [Fact]
    public async Task Exception_CanBeResolvedOnlyOnce()
    {
        await Apply820(new Synthetic820.Transaction { Amount = 10.00m, Trace = "ONCE-1" }.Organization().Rmr("UNKNOWN", 10.00m));
        var queued = (await _exceptions.ListAsync()).Single();
        var dismiss = new ResolveRemittanceExceptionRequest { Action = RemittanceExceptionAction.Dismiss, Note = "Returned to payer" };
        await _service.ResolveExceptionAsync(queued.Id, dismiss);

        var again = () => _service.ResolveExceptionAsync(queued.Id, dismiss);

        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already Dismissed*");
    }

    [Fact]
    public async Task ConcurrentCredits_ToOneAccount_AreAllKept()
    {
        await Task.WhenAll(Enumerable.Range(1, 8).Select(i => _accounts.UpdateAsync(Group, a =>
        {
            a.UnappliedCredit += 10m;
            a.Entries.Add(new SponsorAccountEntry { Amount = 10m, Memo = $"credit {i}" });
        })));

        var account = (await _accounts.GetAsync(Group))!;
        account.UnappliedCredit.Should().Be(80m);
        account.Entries.Should().HaveCount(8);
    }
}
