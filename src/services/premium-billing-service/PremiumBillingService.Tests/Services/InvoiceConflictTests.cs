using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;
using PremiumBillingService.Tests.Remittance;

namespace PremiumBillingService.Tests.Services;

/// <summary>
/// Invoice writers other than cash application, now that invoice saves are
/// version-checked: a concurrent change must be re-read and re-applied, never
/// lost and never abort unrelated work. Against a real mongod.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class InvoiceConflictTests : IAsyncLifetime
{
    private const string Tenant = "tenant-conflict";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private IPremiumInvoiceRepository _store = null!;
    private HookedInvoiceRepository _invoices = null!;
    private readonly Dictionary<string, EftDraft> _drafts = new();
    private readonly Mock<IEftDraftRepository> _draftRepo = new();
    private readonly Mock<ICurrentActor> _actor = new();

    public InvoiceConflictTests(MongoRunnerFixture mongo) => _mongo = mongo;

    private sealed class RequestContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("pb_invoice_conflict");
        var http = new RequestContext { HttpContext = new DefaultHttpContext() };
        http.HttpContext.Items["TenantId"] = Tenant;
        _store = new PremiumInvoiceRepositoryMongo(_database, http, NullLogger<PremiumInvoiceRepositoryMongo>.Instance);
        _invoices = new HookedInvoiceRepository(_store);

        _draftRepo.Setup(r => r.GetByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => _drafts.TryGetValue(id, out var d) ? Copy(d) : null);
        _draftRepo.Setup(r => r.UpdateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => { _drafts[d.Id] = Copy(d); return d; });

        _actor.SetupGet(a => a.UserId).Returns("finance-user-1");
        _actor.SetupGet(a => a.TenantId).Returns(Tenant);
        _actor.SetupGet(a => a.IsAuthenticated).Returns(true);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private static EftDraft Copy(EftDraft d) =>
        System.Text.Json.JsonSerializer.Deserialize<EftDraft>(System.Text.Json.JsonSerializer.Serialize(d))!;

    private EftDraftService Eft() => new(
        _draftRepo.Object, _invoices, Mock.Of<IBillingRunRepository>(), Mock.Of<INachaFileService>(),
        Mock.Of<IStripeAchService>(), Mock.Of<ISponsorBankAccountSource>(), new RecordingNachaDispatcher(),
        _actor.Object, new RequestContext { HttpContext = new DefaultHttpContext() },
        new ConfigurationBuilder().Build(), NullLogger<EftDraftService>.Instance);

    private async Task<PremiumInvoice> Invoice(string number, decimal amount, DateTime? dueDate = null, string group = "GRP001")
    {
        var invoice = new PremiumInvoice
        {
            InvoiceNumber = number,
            GroupNumber = group,
            BillingPeriodStart = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = dueDate ?? new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            GracePeriodExpires = DateTime.UtcNow.AddDays(30),
            Status = InvoiceStatus.Sent,
            LineItems = { new InvoiceLineItem { MemberId = "MBR-1", TotalPremium = amount } }
        };
        invoice.RecalculateTotals();
        return await _store.CreateAsync(invoice);
    }

    private EftDraft Draft(PremiumInvoice invoice, decimal amount, string trace = "091000010000001")
    {
        var draft = new EftDraft
        {
            Id = $"d-{invoice.InvoiceNumber}", InvoiceId = invoice.Id, InvoiceNumber = invoice.InvoiceNumber,
            Status = EftDraftStatus.Submitted, Amount = amount, Method = EftMethod.Nacha, TraceNumber = trace
        };
        _drafts[draft.Id] = Copy(draft);
        return draft;
    }

    private async Task PayByHand(string invoiceId, decimal amount, string reference)
    {
        var other = (await _store.GetByIdAsync(invoiceId))!;
        other.Payments.Add(new InvoicePayment { Amount = amount, PaymentDate = DateTime.UtcNow, ReferenceNumber = reference });
        other.RecalculateTotals();
        PremiumInvoiceStatus.ApplyPaymentStatus(other);
        await _store.UpdateAsync(other);
    }

    [Fact]
    public async Task Settle_WithAConcurrentPayment_RecordsBoth()
    {
        var invoice = await Invoice("INV-EFT-1", 1000m);
        var draft = Draft(invoice, 600m);
        _invoices.BeforeUpdate = async (n, _) =>
        {
            if (n == 1) await PayByHand(invoice.Id, 300m, "CHECK-7"); // lands between our read and our save
        };

        await Eft().SettleDraftAsync(draft.Id);

        var saved = (await _store.GetByIdAsync(invoice.Id))!;
        saved.Payments.Select(p => p.ReferenceNumber).Should().BeEquivalentTo("CHECK-7", "091000010000001");
        saved.TotalPaid.Should().Be(900m);
        saved.BalanceDue.Should().Be(100m);
        saved.Status.Should().Be(InvoiceStatus.PartiallyPaid);
        _drafts[draft.Id].Status.Should().Be(EftDraftStatus.Settled);
    }

    [Fact]
    public async Task Settle_RetriedAfterTheInvoiceSaveFailed_RecordsThePaymentOnce()
    {
        var invoice = await Invoice("INV-EFT-2", 1000m);
        var draft = Draft(invoice, 1000m);
        _invoices.BeforeUpdate = (_, _) => throw new TimeoutException("database went away");

        await FluentActions.Invoking(() => Eft().SettleDraftAsync(draft.Id)).Should().ThrowAsync<TimeoutException>();
        _drafts[draft.Id].Status.Should().Be(EftDraftStatus.Settled);         // the draft was saved…
        (await _store.GetByIdAsync(invoice.Id))!.Payments.Should().BeEmpty(); // …the payment was not

        _invoices.BeforeUpdate = null;
        await Eft().SettleDraftAsync(draft.Id); // previously refused: "Cannot settle draft in Settled state"
        await Eft().SettleDraftAsync(draft.Id); // and again: nothing more to do

        var saved = (await _store.GetByIdAsync(invoice.Id))!;
        saved.Payments.Should().ContainSingle().Which.Amount.Should().Be(1000m);
        saved.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task AchReturn_WithAConcurrentPayment_RemovesOnlyTheDraftPayment()
    {
        var invoice = await Invoice("INV-EFT-3", 1000m);
        var draft = Draft(invoice, 600m);
        await Eft().SettleDraftAsync(draft.Id);
        var stored = _drafts[draft.Id];
        stored.Status = EftDraftStatus.Processing; // the bank returns it before final settlement in this test
        _invoices.BeforeUpdate = async (n, _) =>
        {
            if (n == 2) await PayByHand(invoice.Id, 300m, "CHECK-8");
        };

        await Eft().ProcessAchReturnAsync(new ProcessAchReturnRequest { DraftId = draft.Id, ReturnCode = "R01" });

        var saved = (await _store.GetByIdAsync(invoice.Id))!;
        saved.Payments.Select(p => p.ReferenceNumber).Should().Equal("CHECK-8");
        saved.BalanceDue.Should().Be(700m);
    }

    [Fact]
    public async Task DelinquencyRun_OneInvoiceConflictingAndOneFailing_TheOthersAreStillProcessed()
    {
        var past = DateTime.UtcNow.AddDays(-10);
        var a = await Invoice("INV-A", 100m, past);
        var paidMeanwhile = await Invoice("INV-B", 200m, past);
        var broken = await Invoice("INV-C", 300m, past);
        var d = await Invoice("INV-D", 400m, past);
        _invoices.BeforeUpdate = async (_, invoice) =>
        {
            if (invoice.Id == paidMeanwhile.Id && invoice.TotalPaid == 0)
                await PayByHand(paidMeanwhile.Id, 200m, "PAID-IN-FULL"); // a payment lands while the run saves it
            if (invoice.Id == broken.Id)
                throw new TimeoutException("database went away");
        };
        var service = new PremiumBillingService.Services.PremiumBillingService(
            Mock.Of<IBillingRunRepository>(), _invoices, Mock.Of<ISponsorServiceClient>(), Mock.Of<ICoverageServiceClient>(),
            _actor.Object, NullLogger<PremiumBillingService.Services.PremiumBillingService>.Instance);

        var result = await service.ProcessDelinquenciesAsync();

        (await _store.GetByIdAsync(a.Id))!.Status.Should().Be(InvoiceStatus.Overdue);
        (await _store.GetByIdAsync(d.Id))!.Status.Should().Be(InvoiceStatus.Overdue);
        var b = (await _store.GetByIdAsync(paidMeanwhile.Id))!;
        b.Status.Should().Be(InvoiceStatus.Paid);          // the payment is kept, not overwritten with Overdue
        b.Payments.Should().ContainSingle();
        result.SkippedAfterConflict.Should().Be(1);
        result.InvoiceFailures.Should().ContainSingle().Which.InvoiceNumber.Should().Be("INV-C");
    }

    [Fact]
    public async Task RecordPayment_WithAConcurrentPayment_KeepsBoth()
    {
        var invoice = await Invoice("INV-MAN-1", 1000m);
        _invoices.BeforeUpdate = async (n, _) =>
        {
            if (n == 1) await PayByHand(invoice.Id, 250m, "OTHER");
        };
        var service = new PremiumBillingService.Services.PremiumBillingService(
            Mock.Of<IBillingRunRepository>(), _invoices, Mock.Of<ISponsorServiceClient>(), Mock.Of<ICoverageServiceClient>(),
            _actor.Object, NullLogger<PremiumBillingService.Services.PremiumBillingService>.Instance);

        await service.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 750m, PaymentDate = DateTime.UtcNow, ReferenceNumber = "MINE" });

        var saved = (await _store.GetByIdAsync(invoice.Id))!;
        saved.Payments.Select(p => p.ReferenceNumber).Should().BeEquivalentTo("OTHER", "MINE");
        saved.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task RecordPayment_ThatKeepsConflicting_SurfacesAConflict()
    {
        var invoice = await Invoice("INV-MAN-2", 1000m);
        var n = 0;
        _invoices.BeforeUpdate = async (_, _) => await PayByHand(invoice.Id, 1m, $"OTHER-{++n}");
        var service = new PremiumBillingService.Services.PremiumBillingService(
            Mock.Of<IBillingRunRepository>(), _invoices, Mock.Of<ISponsorServiceClient>(), Mock.Of<ICoverageServiceClient>(),
            _actor.Object, NullLogger<PremiumBillingService.Services.PremiumBillingService>.Instance);

        var act = () => service.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 10m, PaymentDate = DateTime.UtcNow });

        await act.Should().ThrowAsync<ConcurrencyConflictException>(); // the controller answers 409
    }
}
