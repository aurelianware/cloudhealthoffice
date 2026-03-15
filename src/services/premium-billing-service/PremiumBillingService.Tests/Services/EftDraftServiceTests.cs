using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Services;

public class EftDraftServiceTests
{
    private readonly Mock<IEftDraftRepository> _draftRepo;
    private readonly Mock<IPremiumInvoiceRepository> _invoiceRepo;
    private readonly Mock<IBillingRunRepository> _billingRunRepo;
    private readonly Mock<INachaFileService> _nachaService;
    private readonly Mock<IStripeAchService> _stripeService;
    private readonly Mock<IHttpClientFactory> _httpClientFactory;
    private readonly EftDraftService _service;

    public EftDraftServiceTests()
    {
        _draftRepo = new Mock<IEftDraftRepository>();
        _invoiceRepo = new Mock<IPremiumInvoiceRepository>();
        _billingRunRepo = new Mock<IBillingRunRepository>();
        _nachaService = new Mock<INachaFileService>();
        _stripeService = new Mock<IStripeAchService>();
        _httpClientFactory = new Mock<IHttpClientFactory>();

        var configData = new Dictionary<string, string?>
        {
            { "Nacha:ImmediateDestination", "091000019" },
            { "Nacha:ImmediateOrigin", "1234567890" },
            { "Nacha:ImmediateDestinationName", "TEST BANK" },
            { "Nacha:ImmediateOriginName", "HEALTH PLAN" },
            { "Nacha:CompanyName", "HEALTH PLAN" },
            { "Nacha:CompanyId", "1234567890" },
            { "Nacha:OriginatingDfi", "9100001" }
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var logger = new Mock<ILogger<EftDraftService>>();

        _service = new EftDraftService(
            _draftRepo.Object,
            _invoiceRepo.Object,
            _billingRunRepo.Object,
            _nachaService.Object,
            _stripeService.Object,
            _httpClientFactory.Object,
            configuration,
            logger.Object);
    }

    private static PremiumInvoice CreateInvoice(
        string id = "inv-1",
        decimal balanceDue = 1500.00m,
        InvoiceStatus status = InvoiceStatus.Sent) => new()
    {
        Id = id,
        InvoiceNumber = "INV-GRP001-2026-03",
        GroupNumber = "GRP001",
        SponsorName = "ACME Corp",
        Status = status,
        BalanceDue = balanceDue,
        TotalAmount = balanceDue,
        DueDate = DateTime.UtcNow.AddDays(30)
    };

    private void SetupSponsorBankAccountResponse(SponsorBankAccount? bankAccount)
    {
        var handler = new MockHttpMessageHandler(bankAccount);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://sponsor-service") };
        _httpClientFactory.Setup(f => f.CreateClient("SponsorService")).Returns(client);
    }

    #region InitiateDraftAsync

    [Fact]
    public async Task InitiateDraftAsync_WithValidNachaDraft_CreatesPendingDraft()
    {
        var invoice = CreateInvoice();
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        SetupSponsorBankAccountResponse(new SponsorBankAccount
        {
            EftEnabled = true,
            PreferredMethod = EftMethod.Nacha,
            RoutingNumber = "091000019",
            AccountNumber = "123456789",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "6789"
        });
        _draftRepo.Setup(r => r.CreateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);

        var result = await _service.InitiateDraftAsync(new InitiateEftDraftRequest
        {
            InvoiceId = "inv-1",
            InitiatedBy = "admin"
        });

        result.Method.Should().Be(EftMethod.Nacha);
        result.Status.Should().Be(EftDraftStatus.Pending);
        result.Amount.Should().Be(1500.00m);
        result.InvoiceId.Should().Be("inv-1");
    }

    [Fact]
    public async Task InitiateDraftAsync_WithPaidInvoice_Throws()
    {
        var invoice = CreateInvoice(status: InvoiceStatus.Paid);
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);

        var act = () => _service.InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "inv-1" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Paid*");
    }

    [Fact]
    public async Task InitiateDraftAsync_WithVoidedInvoice_Throws()
    {
        var invoice = CreateInvoice(status: InvoiceStatus.Voided);
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);

        var act = () => _service.InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "inv-1" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Voided*");
    }

    [Fact]
    public async Task InitiateDraftAsync_WithZeroBalance_Throws()
    {
        var invoice = CreateInvoice(balanceDue: 0);
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);

        var act = () => _service.InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "inv-1" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no balance due*");
    }

    [Fact]
    public async Task InitiateDraftAsync_WithNonexistentInvoice_Throws()
    {
        _invoiceRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((PremiumInvoice?)null);

        var act = () => _service.InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "missing" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task InitiateDraftAsync_WithEftDisabled_Throws()
    {
        var invoice = CreateInvoice();
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        SetupSponsorBankAccountResponse(new SponsorBankAccount { EftEnabled = false });

        var act = () => _service.InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "inv-1" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*EFT not enabled*");
    }

    [Fact]
    public async Task InitiateDraftAsync_WithCustomAmount_UsesOverrideAmount()
    {
        var invoice = CreateInvoice(balanceDue: 1500.00m);
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        SetupSponsorBankAccountResponse(new SponsorBankAccount
        {
            EftEnabled = true,
            RoutingNumber = "091000019",
            AccountNumber = "123456789",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "6789"
        });
        _draftRepo.Setup(r => r.CreateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);

        var result = await _service.InitiateDraftAsync(new InitiateEftDraftRequest
        {
            InvoiceId = "inv-1",
            Amount = 500.00m
        });

        result.Amount.Should().Be(500.00m);
    }

    [Fact]
    public async Task InitiateDraftAsync_StripeAch_SubmitsImmediately()
    {
        var invoice = CreateInvoice();
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        SetupSponsorBankAccountResponse(new SponsorBankAccount
        {
            EftEnabled = true,
            PreferredMethod = EftMethod.StripeAch,
            StripeCustomerId = "cus_123",
            StripePaymentMethodId = "pm_123",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "6789"
        });
        _stripeService.Setup(s => s.CreateAchDraftAsync(
                "cus_123", "pm_123", 1500.00m, It.IsAny<string>(), "GRP001"))
            .ReturnsAsync(new StripeAchDraftResult
            {
                PaymentIntentId = "pi_123",
                Status = "processing"
            });
        _draftRepo.Setup(r => r.CreateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);

        var result = await _service.InitiateDraftAsync(new InitiateEftDraftRequest
        {
            InvoiceId = "inv-1",
            Method = EftMethod.StripeAch
        });

        result.Status.Should().Be(EftDraftStatus.Submitted);
        result.StripePaymentIntentId.Should().Be("pi_123");
        result.SubmittedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task InitiateDraftAsync_StripeAchFailed_SetsFailedStatus()
    {
        var invoice = CreateInvoice();
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        SetupSponsorBankAccountResponse(new SponsorBankAccount
        {
            EftEnabled = true,
            PreferredMethod = EftMethod.StripeAch,
            StripeCustomerId = "cus_123",
            StripePaymentMethodId = "pm_123",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "6789"
        });
        _stripeService.Setup(s => s.CreateAchDraftAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new StripeAchDraftResult
            {
                Status = "failed",
                ErrorMessage = "Insufficient funds"
            });
        _draftRepo.Setup(r => r.CreateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);

        var result = await _service.InitiateDraftAsync(new InitiateEftDraftRequest
        {
            InvoiceId = "inv-1",
            Method = EftMethod.StripeAch
        });

        result.Status.Should().Be(EftDraftStatus.Failed);
        result.ErrorMessage.Should().Be("Insufficient funds");
    }

    #endregion

    #region InitiateBatchDraftAsync

    [Fact]
    public async Task InitiateBatchDraftAsync_DeduplicatesInvoiceIds()
    {
        SetupSponsorBankAccountResponse(new SponsorBankAccount
        {
            EftEnabled = true,
            RoutingNumber = "091000019",
            AccountNumber = "123456789",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "6789"
        });
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(CreateInvoice("inv-1"));
        _draftRepo.Setup(r => r.CreateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);
        _nachaService.Setup(s => s.GenerateNachaFile(It.IsAny<List<NachaEntryDetail>>(), It.IsAny<NachaFileOptions>()))
            .Returns(new NachaFileResult { FileReference = "NACHA-TEST" });

        var result = await _service.InitiateBatchDraftAsync(new InitiateBatchEftRequest
        {
            InvoiceIds = new List<string> { "inv-1", "inv-1", "inv-1" },
            InitiatedBy = "admin"
        });

        result.TotalInvoices.Should().Be(1);
        result.DraftsInitiated.Should().Be(1);
    }

    [Fact]
    public async Task InitiateBatchDraftAsync_SkipsInvalidInvoices()
    {
        SetupSponsorBankAccountResponse(new SponsorBankAccount
        {
            EftEnabled = true,
            RoutingNumber = "091000019",
            AccountNumber = "123456789",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "6789"
        });
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(CreateInvoice("inv-1"));
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-paid")).ReturnsAsync(CreateInvoice("inv-paid", status: InvoiceStatus.Paid));
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-zero")).ReturnsAsync(CreateInvoice("inv-zero", balanceDue: 0));
        _draftRepo.Setup(r => r.CreateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);
        _nachaService.Setup(s => s.GenerateNachaFile(It.IsAny<List<NachaEntryDetail>>(), It.IsAny<NachaFileOptions>()))
            .Returns(new NachaFileResult { FileReference = "NACHA-TEST" });

        var result = await _service.InitiateBatchDraftAsync(new InitiateBatchEftRequest
        {
            InvoiceIds = new List<string> { "inv-1", "inv-paid", "inv-zero" },
            InitiatedBy = "admin"
        });

        result.DraftsInitiated.Should().Be(1);
        result.Skipped.Should().Be(2);
    }

    [Fact]
    public async Task InitiateBatchDraftAsync_NachaDrafts_AssignsTraceNumbers()
    {
        SetupSponsorBankAccountResponse(new SponsorBankAccount
        {
            EftEnabled = true,
            RoutingNumber = "091000019",
            AccountNumber = "123456789",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "6789"
        });
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(CreateInvoice("inv-1"));
        _draftRepo.Setup(r => r.CreateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);

        var capturedEntries = new List<NachaEntryDetail>();
        _nachaService.Setup(s => s.GenerateNachaFile(It.IsAny<List<NachaEntryDetail>>(), It.IsAny<NachaFileOptions>()))
            .Callback<List<NachaEntryDetail>, NachaFileOptions>((entries, _) =>
            {
                capturedEntries = entries;
                foreach (var e in entries)
                    e.TraceNumber = "0910000100001";
            })
            .Returns(new NachaFileResult { FileReference = "NACHA-TEST" });

        await _service.InitiateBatchDraftAsync(new InitiateBatchEftRequest
        {
            InvoiceIds = new List<string> { "inv-1" },
            InitiatedBy = "admin"
        });

        _draftRepo.Verify(r => r.UpdateAsync(It.Is<EftDraft>(d =>
            d.TraceNumber == "0910000100001" &&
            d.Status == EftDraftStatus.Submitted &&
            d.NachaFileReference == "NACHA-TEST")), Times.Once);
    }

    [Fact]
    public async Task InitiateBatchDraftAsync_WithBillingRun_IncludesRunInvoices()
    {
        var billingRun = new BillingRun { InvoiceIds = new List<string> { "inv-1" } };
        _billingRunRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(billingRun);
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(CreateInvoice("inv-1"));
        SetupSponsorBankAccountResponse(new SponsorBankAccount
        {
            EftEnabled = true,
            RoutingNumber = "091000019",
            AccountNumber = "123456789",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "6789"
        });
        _draftRepo.Setup(r => r.CreateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);
        _nachaService.Setup(s => s.GenerateNachaFile(It.IsAny<List<NachaEntryDetail>>(), It.IsAny<NachaFileOptions>()))
            .Returns(new NachaFileResult { FileReference = "NACHA-TEST" });

        var result = await _service.InitiateBatchDraftAsync(new InitiateBatchEftRequest
        {
            BillingRunId = "run-1",
            InitiatedBy = "admin"
        });

        result.DraftsInitiated.Should().Be(1);
    }

    #endregion

    #region GenerateNachaFileForPendingDraftsAsync

    [Fact]
    public async Task GenerateNachaFile_NoPendingDrafts_Throws()
    {
        _draftRepo.Setup(r => r.GetByStatusAsync(EftDraftStatus.Pending))
            .ReturnsAsync(Enumerable.Empty<EftDraft>());

        var act = () => _service.GenerateNachaFileForPendingDraftsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No pending NACHA drafts*");
    }

    [Fact]
    public async Task GenerateNachaFile_SkipsDraftsWithMissingBankDetails()
    {
        var drafts = new List<EftDraft>
        {
            new() { Id = "d1", GroupNumber = "GRP001", Method = EftMethod.Nacha, Amount = 100 },
            new() { Id = "d2", GroupNumber = "GRP002", Method = EftMethod.Nacha, Amount = 200 }
        };
        _draftRepo.Setup(r => r.GetByStatusAsync(EftDraftStatus.Pending)).ReturnsAsync(drafts);

        // GRP001 has valid bank, GRP002 has no routing number
        var handler = new MockHttpMessageHandler(request =>
        {
            if (request.RequestUri!.PathAndQuery.Contains("GRP001"))
                return new SponsorBankAccount
                {
                    EftEnabled = true,
                    RoutingNumber = "091000019",
                    AccountNumber = "123456789"
                };
            return new SponsorBankAccount
            {
                EftEnabled = true,
                RoutingNumber = null,
                AccountNumber = null
            };
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://sponsor-service") };
        _httpClientFactory.Setup(f => f.CreateClient("SponsorService")).Returns(client);

        _nachaService.Setup(s => s.GenerateNachaFile(
                It.Is<List<NachaEntryDetail>>(e => e.Count == 1),
                It.IsAny<NachaFileOptions>()))
            .Returns(new NachaFileResult { FileReference = "NACHA-TEST" });

        await _service.GenerateNachaFileForPendingDraftsAsync();

        // Only the draft with valid bank should be updated
        _draftRepo.Verify(r => r.UpdateAsync(It.Is<EftDraft>(d =>
            d.Id == "d1" && d.Status == EftDraftStatus.Submitted)), Times.Once);
        // Draft with missing bank should NOT be updated
        _draftRepo.Verify(r => r.UpdateAsync(It.Is<EftDraft>(d =>
            d.Id == "d2")), Times.Never);
    }

    [Fact]
    public async Task GenerateNachaFile_AllDraftsMissingBankDetails_Throws()
    {
        var drafts = new List<EftDraft>
        {
            new() { Id = "d1", GroupNumber = "GRP001", Method = EftMethod.Nacha, Amount = 100 }
        };
        _draftRepo.Setup(r => r.GetByStatusAsync(EftDraftStatus.Pending)).ReturnsAsync(drafts);

        SetupSponsorBankAccountResponse(new SponsorBankAccount
        {
            EftEnabled = true,
            RoutingNumber = null,
            AccountNumber = null
        });

        var act = () => _service.GenerateNachaFileForPendingDraftsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No drafts with valid bank accounts*");
    }

    [Fact]
    public async Task GenerateNachaFile_OnlyIncludesNachaDrafts()
    {
        var drafts = new List<EftDraft>
        {
            new() { Id = "d1", GroupNumber = "GRP001", Method = EftMethod.Nacha, Amount = 100 },
            new() { Id = "d2", GroupNumber = "GRP001", Method = EftMethod.StripeAch, Amount = 200 }
        };
        _draftRepo.Setup(r => r.GetByStatusAsync(EftDraftStatus.Pending)).ReturnsAsync(drafts);
        SetupSponsorBankAccountResponse(new SponsorBankAccount
        {
            EftEnabled = true,
            RoutingNumber = "091000019",
            AccountNumber = "123456789"
        });
        _nachaService.Setup(s => s.GenerateNachaFile(
                It.Is<List<NachaEntryDetail>>(e => e.Count == 1),
                It.IsAny<NachaFileOptions>()))
            .Returns(new NachaFileResult { FileReference = "NACHA-TEST" });

        await _service.GenerateNachaFileForPendingDraftsAsync();

        _nachaService.Verify(s => s.GenerateNachaFile(
            It.Is<List<NachaEntryDetail>>(e => e.Count == 1),
            It.IsAny<NachaFileOptions>()), Times.Once);
    }

    #endregion

    #region SettleDraftAsync

    [Fact]
    public async Task SettleDraftAsync_SubmittedDraft_Settles()
    {
        var draft = new EftDraft
        {
            Id = "d1", InvoiceId = "inv-1", InvoiceNumber = "INV-001",
            Status = EftDraftStatus.Submitted, Amount = 1000, Method = EftMethod.Nacha
        };
        _draftRepo.Setup(r => r.GetByIdAsync("d1")).ReturnsAsync(draft);
        _draftRepo.Setup(r => r.UpdateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);
        var invoice = CreateInvoice();
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice i) => i);

        var result = await _service.SettleDraftAsync("d1");

        result.Status.Should().Be(EftDraftStatus.Settled);
        result.SettledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SettleDraftAsync_RecordsPaymentOnInvoice()
    {
        var draft = new EftDraft
        {
            Id = "d1", InvoiceId = "inv-1", InvoiceNumber = "INV-001",
            Status = EftDraftStatus.Submitted, Amount = 1500, Method = EftMethod.Nacha,
            TraceNumber = "TRACE123"
        };
        _draftRepo.Setup(r => r.GetByIdAsync("d1")).ReturnsAsync(draft);
        _draftRepo.Setup(r => r.UpdateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);

        var invoice = CreateInvoice();
        invoice.LineItems.Add(new InvoiceLineItem { MemberId = "m1", TotalPremium = 1500 });
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        PremiumInvoice? savedInvoice = null;
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()))
            .Callback<PremiumInvoice>(i => savedInvoice = i)
            .ReturnsAsync((PremiumInvoice i) => i);

        await _service.SettleDraftAsync("d1");

        savedInvoice.Should().NotBeNull();
        savedInvoice!.Payments.Should().ContainSingle();
        savedInvoice.Payments[0].Amount.Should().Be(1500);
        savedInvoice.Payments[0].ReferenceNumber.Should().Be("TRACE123");
        savedInvoice.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task SettleDraftAsync_PendingDraft_Throws()
    {
        var draft = new EftDraft { Id = "d1", Status = EftDraftStatus.Pending };
        _draftRepo.Setup(r => r.GetByIdAsync("d1")).ReturnsAsync(draft);

        var act = () => _service.SettleDraftAsync("d1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Pending*");
    }

    #endregion

    #region ProcessAchReturnAsync

    [Fact]
    public async Task ProcessAchReturnAsync_SetsReturnStatus()
    {
        var draft = new EftDraft
        {
            Id = "d1", InvoiceId = "inv-1", InvoiceNumber = "INV-001",
            Status = EftDraftStatus.Submitted, Amount = 1000
        };
        _draftRepo.Setup(r => r.GetByIdAsync("d1")).ReturnsAsync(draft);
        _draftRepo.Setup(r => r.UpdateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(CreateInvoice());
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice i) => i);

        var result = await _service.ProcessAchReturnAsync(new ProcessAchReturnRequest
        {
            DraftId = "d1",
            ReturnCode = "R01"
        });

        result.Status.Should().Be(EftDraftStatus.Returned);
        result.ReturnCode.Should().Be("R01");
        result.ReturnReason.Should().Be("Insufficient Funds");
        result.ReturnedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAchReturnAsync_PendingDraft_Throws()
    {
        var draft = new EftDraft { Id = "d1", Status = EftDraftStatus.Pending };
        _draftRepo.Setup(r => r.GetByIdAsync("d1")).ReturnsAsync(draft);

        var act = () => _service.ProcessAchReturnAsync(new ProcessAchReturnRequest
        {
            DraftId = "d1",
            ReturnCode = "R01"
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region CancelDraftAsync

    [Fact]
    public async Task CancelDraftAsync_PendingDraft_Cancels()
    {
        var draft = new EftDraft { Id = "d1", Status = EftDraftStatus.Pending, Method = EftMethod.Nacha };
        _draftRepo.Setup(r => r.GetByIdAsync("d1")).ReturnsAsync(draft);
        _draftRepo.Setup(r => r.UpdateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);

        var result = await _service.CancelDraftAsync("d1");

        result.Status.Should().Be(EftDraftStatus.Cancelled);
    }

    [Fact]
    public async Task CancelDraftAsync_SubmittedDraft_Throws()
    {
        var draft = new EftDraft { Id = "d1", Status = EftDraftStatus.Submitted };
        _draftRepo.Setup(r => r.GetByIdAsync("d1")).ReturnsAsync(draft);

        var act = () => _service.CancelDraftAsync("d1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Pending*");
    }

    [Fact]
    public async Task CancelDraftAsync_StripeAch_CancelsPaymentIntent()
    {
        var draft = new EftDraft
        {
            Id = "d1", Status = EftDraftStatus.Pending,
            Method = EftMethod.StripeAch, StripePaymentIntentId = "pi_123"
        };
        _draftRepo.Setup(r => r.GetByIdAsync("d1")).ReturnsAsync(draft);
        _draftRepo.Setup(r => r.UpdateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);

        await _service.CancelDraftAsync("d1");

        _stripeService.Verify(s => s.CancelDraftAsync("pi_123"), Times.Once);
    }

    #endregion

    #region ProcessStripeWebhookAsync

    [Fact]
    public async Task ProcessStripeWebhookAsync_PaymentSucceeded_SettlesDraft()
    {
        var draft = new EftDraft
        {
            Id = "d1", InvoiceId = "inv-1", InvoiceNumber = "INV-001",
            Status = EftDraftStatus.Submitted, Amount = 1000,
            StripePaymentIntentId = "pi_123", Method = EftMethod.StripeAch
        };
        _draftRepo.Setup(r => r.GetByStripePaymentIntentIdAsync("pi_123"))
            .ReturnsAsync(new[] { draft });
        _draftRepo.Setup(r => r.GetByIdAsync("d1")).ReturnsAsync(draft);
        _draftRepo.Setup(r => r.UpdateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => d);
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(CreateInvoice());
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice i) => i);
        _stripeService.Setup(s => s.ProcessWebhookAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EftWebhookResult
            {
                Handled = true,
                EventType = "payment_succeeded",
                PaymentIntentId = "pi_123"
            });

        await _service.ProcessStripeWebhookAsync("{}", "sig_test");

        _draftRepo.Verify(r => r.UpdateAsync(It.Is<EftDraft>(d =>
            d.Status == EftDraftStatus.Settled)), Times.Once);
    }

    [Fact]
    public async Task ProcessStripeWebhookAsync_UnhandledEvent_DoesNothing()
    {
        _stripeService.Setup(s => s.ProcessWebhookAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EftWebhookResult { Handled = false });

        await _service.ProcessStripeWebhookAsync("{}", "sig_test");

        _draftRepo.Verify(r => r.UpdateAsync(It.IsAny<EftDraft>()), Times.Never);
    }

    #endregion
}

/// <summary>
/// Mock HTTP handler for simulating sponsor-service responses
/// </summary>
internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, SponsorBankAccount?> _responseFactory;

    public MockHttpMessageHandler(SponsorBankAccount? fixedResponse)
        : this(_ => fixedResponse) { }

    public MockHttpMessageHandler(Func<HttpRequestMessage, SponsorBankAccount?> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bankAccount = _responseFactory(request);
        if (bankAccount == null)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var json = JsonSerializer.Serialize(bankAccount);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });
    }
}
