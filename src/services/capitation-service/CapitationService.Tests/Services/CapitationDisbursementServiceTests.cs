using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;

namespace CapitationService.Tests.Services;

public class CapitationDisbursementServiceTests
{
    private readonly Mock<ICapitationDisbursementRepository> _disbursementRepo;
    private readonly Mock<ICapitationStatementRepository> _statementRepo;
    private readonly Mock<ICapitationRunRepository> _runRepo;
    private readonly Mock<INachaCreditFileService> _nachaService;
    private readonly Mock<IStripeConnectService> _stripeService;
    private readonly Mock<IHttpClientFactory> _httpClientFactory;
    private readonly CapitationDisbursementService _service;

    public CapitationDisbursementServiceTests()
    {
        _disbursementRepo = new Mock<ICapitationDisbursementRepository>();
        _statementRepo = new Mock<ICapitationStatementRepository>();
        _runRepo = new Mock<ICapitationRunRepository>();
        _nachaService = new Mock<INachaCreditFileService>();
        _stripeService = new Mock<IStripeConnectService>();
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

        var logger = new Mock<ILogger<CapitationDisbursementService>>();

        _service = new CapitationDisbursementService(
            _disbursementRepo.Object,
            _statementRepo.Object,
            _runRepo.Object,
            _nachaService.Object,
            _stripeService.Object,
            _httpClientFactory.Object,
            configuration,
            logger.Object);
    }

    private static CapitationStatement CreateApprovedStatement(
        string id = "stmt-1",
        decimal netPayable = 5000.00m) => new()
    {
        Id = id,
        StatementNumber = "CAPSTMT-1234567890-2026-03",
        ProviderNPI = "1234567890",
        ProviderName = "Dr. Smith",
        Status = CapitationStatementStatus.Approved,
        NetPayable = netPayable,
        GrossCapitation = netPayable / 0.9m, // Assuming 10% withhold
        WithholdAmount = netPayable / 0.9m * 0.1m
    };

    private void SetupProviderBankAccountResponse(ProviderBankAccountDto? bankAccount)
    {
        var handler = new MockHttpMessageHandler<ProviderBankAccountDto>(_ => bankAccount);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://provider-service") };
        _httpClientFactory.Setup(f => f.CreateClient("ProviderService")).Returns(client);
    }

    #region InitiateDisbursementAsync

    [Fact]
    public async Task InitiateDisbursementAsync_NachaCredit_CreatesPendingDisbursement()
    {
        var statement = CreateApprovedStatement();
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);
        SetupProviderBankAccountResponse(new ProviderBankAccountDto
        {
            EftEnabled = true,
            PreferredDisbursementMethod = "NachaCredit",
            RoutingNumber = "091000019",
            AccountNumber = "987654321",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "4321"
        });
        _disbursementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        var result = await _service.InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "stmt-1",
            InitiatedBy = "admin"
        });

        result.Method.Should().Be(DisbursementMethod.NachaCredit);
        result.Status.Should().Be(DisbursementStatus.Pending);
        result.Amount.Should().Be(5000.00m);
        result.ProviderNPI.Should().Be("1234567890");
    }

    [Fact]
    public async Task InitiateDisbursementAsync_StripeConnect_CreatesAndSubmits()
    {
        var statement = CreateApprovedStatement();
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);
        SetupProviderBankAccountResponse(new ProviderBankAccountDto
        {
            EftEnabled = true,
            PreferredDisbursementMethod = "StripeConnect",
            StripeConnectedAccountId = "acct_provider123",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "4321"
        });
        _stripeService.Setup(s => s.CreateTransferAsync(
                "acct_provider123", 5000.00m, It.IsAny<string>(), "1234567890"))
            .ReturnsAsync(new StripeTransferResult
            {
                TransferId = "tr_abc123",
                Status = "created"
            });
        _disbursementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        var result = await _service.InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "stmt-1",
            Method = DisbursementMethod.StripeConnect
        });

        result.Status.Should().Be(DisbursementStatus.Submitted);
        result.StripeTransferId.Should().Be("tr_abc123");
        result.SubmittedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task InitiateDisbursementAsync_NotApproved_Throws()
    {
        var statement = CreateApprovedStatement();
        statement.Status = CapitationStatementStatus.Generated;
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);

        var act = () => _service.InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "stmt-1"
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Approved*");
    }

    #endregion

    #region InitiateBatchDisbursementAsync

    [Fact]
    public async Task InitiateBatchDisbursementAsync_MixedMethods_HandlesCorrectly()
    {
        // Two statements: one for NACHA, one for Stripe
        var stmt1 = CreateApprovedStatement("stmt-nacha", 1000.00m);
        var stmt2 = CreateApprovedStatement("stmt-stripe", 2000.00m);
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-nacha")).ReturnsAsync(stmt1);
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-stripe")).ReturnsAsync(stmt2);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);

        // Provider bank account returns NACHA-preferred
        SetupProviderBankAccountResponse(new ProviderBankAccountDto
        {
            EftEnabled = true,
            PreferredDisbursementMethod = "NachaCredit",
            RoutingNumber = "091000019",
            AccountNumber = "987654321",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "4321"
        });

        _disbursementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);
        _nachaService.Setup(s => s.GenerateNachaCreditFile(
                It.IsAny<List<NachaCreditEntryDetail>>(), It.IsAny<NachaCreditFileOptions>()))
            .Returns(new NachaCreditFileResult { FileReference = "NACHA-CR-TEST" });

        var result = await _service.InitiateBatchDisbursementAsync(new InitiateBatchDisbursementRequest
        {
            StatementIds = new List<string> { "stmt-nacha", "stmt-stripe" },
            InitiatedBy = "admin"
        });

        result.TotalStatements.Should().Be(2);
        result.DisbursementsInitiated.Should().Be(2);
        result.TotalAmount.Should().Be(3000.00m);
        result.NachaFile.Should().NotBeNull();
    }

    #endregion

    #region SettleDisbursementAsync

    [Fact]
    public async Task SettleDisbursementAsync_UpdatesStatementToPaid()
    {
        var disbursement = new CapitationDisbursement
        {
            Id = "disb-1", StatementId = "stmt-1", StatementNumber = "CAPSTMT-001",
            Status = DisbursementStatus.Submitted, Amount = 5000,
            Method = DisbursementMethod.NachaCredit
        };
        _disbursementRepo.Setup(r => r.GetByIdAsync("disb-1")).ReturnsAsync(disbursement);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        var statement = CreateApprovedStatement();
        statement.Status = CapitationStatementStatus.PaymentInitiated;
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        var result = await _service.SettleDisbursementAsync("disb-1");

        result.Status.Should().Be(DisbursementStatus.Settled);
        result.SettledAt.Should().NotBeNull();
        savedStatement.Should().NotBeNull();
        savedStatement!.Status.Should().Be(CapitationStatementStatus.Paid);
        savedStatement.PaymentDate.Should().NotBeNull();
    }

    #endregion

    #region ProcessReturnAsync

    [Fact]
    public async Task ProcessReturnAsync_RevertsStatementToApproved()
    {
        var disbursement = new CapitationDisbursement
        {
            Id = "disb-1", StatementId = "stmt-1", StatementNumber = "CAPSTMT-001",
            Status = DisbursementStatus.Submitted, Amount = 5000
        };
        _disbursementRepo.Setup(r => r.GetByIdAsync("disb-1")).ReturnsAsync(disbursement);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        var statement = CreateApprovedStatement();
        statement.Status = CapitationStatementStatus.PaymentInitiated;
        statement.EftDisbursementId = "disb-1";
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        var result = await _service.ProcessReturnAsync(new ProcessReturnRequest
        {
            DisbursementId = "disb-1",
            ReturnCode = "R01"
        });

        result.Status.Should().Be(DisbursementStatus.Returned);
        result.ReturnCode.Should().Be("R01");
        result.ReturnReason.Should().Be("Insufficient Funds");
        savedStatement.Should().NotBeNull();
        savedStatement!.Status.Should().Be(CapitationStatementStatus.Approved);
        savedStatement.EftDisbursementId.Should().BeNull();
    }

    [Fact]
    public async Task ProcessReturnAsync_PendingDisbursement_Throws()
    {
        var disbursement = new CapitationDisbursement
        {
            Id = "disb-1", Status = DisbursementStatus.Pending
        };
        _disbursementRepo.Setup(r => r.GetByIdAsync("disb-1")).ReturnsAsync(disbursement);

        var act = () => _service.ProcessReturnAsync(new ProcessReturnRequest
        {
            DisbursementId = "disb-1", ReturnCode = "R01"
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region CancelDisbursementAsync

    [Fact]
    public async Task CancelDisbursementAsync_PendingOnly()
    {
        var disbursement = new CapitationDisbursement
        {
            Id = "disb-1", StatementId = "stmt-1",
            Status = DisbursementStatus.Pending, Method = DisbursementMethod.NachaCredit
        };
        _disbursementRepo.Setup(r => r.GetByIdAsync("disb-1")).ReturnsAsync(disbursement);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        var statement = CreateApprovedStatement();
        statement.Status = CapitationStatementStatus.PaymentInitiated;
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);

        var result = await _service.CancelDisbursementAsync("disb-1");

        result.Status.Should().Be(DisbursementStatus.Cancelled);
    }

    [Fact]
    public async Task CancelDisbursementAsync_SubmittedDisbursement_Throws()
    {
        var disbursement = new CapitationDisbursement
        {
            Id = "disb-1", Status = DisbursementStatus.Submitted
        };
        _disbursementRepo.Setup(r => r.GetByIdAsync("disb-1")).ReturnsAsync(disbursement);

        var act = () => _service.CancelDisbursementAsync("disb-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Pending*");
    }

    [Fact]
    public async Task CancelDisbursementAsync_StripeConnect_ReversesTransfer()
    {
        var disbursement = new CapitationDisbursement
        {
            Id = "disb-1", StatementId = "stmt-1",
            Status = DisbursementStatus.Pending,
            Method = DisbursementMethod.StripeConnect,
            StripeTransferId = "tr_abc123"
        };
        _disbursementRepo.Setup(r => r.GetByIdAsync("disb-1")).ReturnsAsync(disbursement);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        var statement = CreateApprovedStatement();
        statement.Status = CapitationStatementStatus.PaymentInitiated;
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.CancelDisbursementAsync("disb-1");

        _stripeService.Verify(s => s.CancelTransferAsync("tr_abc123"), Times.Once);
    }

    #endregion

    #region GenerateNachaCreditFileAsync

    [Fact]
    public async Task GenerateNachaCreditFileAsync_NoPendingDisbursements_Throws()
    {
        _disbursementRepo.Setup(r => r.GetByStatusAsync(DisbursementStatus.Pending))
            .ReturnsAsync(Enumerable.Empty<CapitationDisbursement>());

        var act = () => _service.GenerateNachaCreditFileAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No pending NACHA*");
    }

    [Fact]
    public async Task GenerateNachaCreditFileAsync_WithPendingDisbursements_GeneratesFile()
    {
        var disbursements = new List<CapitationDisbursement>
        {
            new() { Id = "d1", ProviderNPI = "1234567890", ProviderName = "Dr. Chen",
                     Method = DisbursementMethod.NachaCredit, Amount = 5000, Status = DisbursementStatus.Pending },
            new() { Id = "d2", ProviderNPI = "9876543210", ProviderName = "Valley Med",
                     Method = DisbursementMethod.NachaCredit, Amount = 8000, Status = DisbursementStatus.Pending }
        };
        _disbursementRepo.Setup(r => r.GetByStatusAsync(DisbursementStatus.Pending)).ReturnsAsync(disbursements);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        // Provider bank accounts
        var handler = new MockHttpMessageHandler<ProviderBankAccountDto>(_ => new ProviderBankAccountDto
        {
            EftEnabled = true,
            RoutingNumber = "091000019",
            AccountNumber = "123456789",
            AccountHolderName = "TEST PROVIDER"
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://provider-service") };
        _httpClientFactory.Setup(f => f.CreateClient("ProviderService")).Returns(client);

        var capturedEntries = new List<NachaCreditEntryDetail>();
        _nachaService.Setup(s => s.GenerateNachaCreditFile(
                It.IsAny<List<NachaCreditEntryDetail>>(),
                It.IsAny<NachaCreditFileOptions>()))
            .Callback<List<NachaCreditEntryDetail>, NachaCreditFileOptions>((entries, _) =>
            {
                capturedEntries = entries;
                for (int i = 0; i < entries.Count; i++)
                    entries[i].TraceNumber = $"091000010000{i + 1}";
            })
            .Returns(new NachaCreditFileResult { FileReference = "NACHA-CR-TEST", EntryCount = 2, TotalAmount = 13000 });

        var result = await _service.GenerateNachaCreditFileAsync();

        result.FileReference.Should().Be("NACHA-CR-TEST");
        result.EntryCount.Should().Be(2);
        capturedEntries.Should().HaveCount(2);

        // Verify disbursements updated to Submitted with trace numbers
        _disbursementRepo.Verify(r => r.UpdateAsync(It.Is<CapitationDisbursement>(d =>
            d.Status == DisbursementStatus.Submitted && d.TraceNumber != null)), Times.Exactly(2));
    }

    [Fact]
    public async Task GenerateNachaCreditFileAsync_SkipsDisbursementsWithMissingBankDetails()
    {
        var disbursements = new List<CapitationDisbursement>
        {
            new() { Id = "d1", ProviderNPI = "1111111111", Method = DisbursementMethod.NachaCredit, Amount = 5000, Status = DisbursementStatus.Pending },
            new() { Id = "d2", ProviderNPI = "2222222222", Method = DisbursementMethod.NachaCredit, Amount = 3000, Status = DisbursementStatus.Pending }
        };
        _disbursementRepo.Setup(r => r.GetByStatusAsync(DisbursementStatus.Pending)).ReturnsAsync(disbursements);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        // First NPI has bank account, second doesn't
        var handler = new MockHttpMessageHandler<ProviderBankAccountDto>(request =>
        {
            if (request.RequestUri!.PathAndQuery.Contains("1111111111"))
                return new ProviderBankAccountDto { EftEnabled = true, RoutingNumber = "091000019", AccountNumber = "123456789" };
            return new ProviderBankAccountDto { EftEnabled = true, RoutingNumber = null, AccountNumber = null };
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://provider-service") };
        _httpClientFactory.Setup(f => f.CreateClient("ProviderService")).Returns(client);

        _nachaService.Setup(s => s.GenerateNachaCreditFile(
                It.Is<List<NachaCreditEntryDetail>>(e => e.Count == 1),
                It.IsAny<NachaCreditFileOptions>()))
            .Returns(new NachaCreditFileResult { FileReference = "NACHA-CR-TEST" });

        await _service.GenerateNachaCreditFileAsync();

        // Only d1 should be updated (d2 skipped due to missing bank)
        _disbursementRepo.Verify(r => r.UpdateAsync(It.Is<CapitationDisbursement>(d =>
            d.Id == "d1" && d.Status == DisbursementStatus.Submitted)), Times.Once);
        _disbursementRepo.Verify(r => r.UpdateAsync(It.Is<CapitationDisbursement>(d =>
            d.Id == "d2")), Times.Never);
    }

    [Fact]
    public async Task GenerateNachaCreditFileAsync_FiltersOnlyNachaDisbursements()
    {
        var disbursements = new List<CapitationDisbursement>
        {
            new() { Id = "d1", ProviderNPI = "1234567890", Method = DisbursementMethod.NachaCredit, Amount = 5000, Status = DisbursementStatus.Pending },
            new() { Id = "d2", ProviderNPI = "1234567890", Method = DisbursementMethod.StripeConnect, Amount = 3000, Status = DisbursementStatus.Pending }
        };
        _disbursementRepo.Setup(r => r.GetByStatusAsync(DisbursementStatus.Pending)).ReturnsAsync(disbursements);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        SetupProviderBankAccountResponse(new ProviderBankAccountDto
        {
            EftEnabled = true, RoutingNumber = "091000019", AccountNumber = "123456789"
        });

        _nachaService.Setup(s => s.GenerateNachaCreditFile(
                It.Is<List<NachaCreditEntryDetail>>(e => e.Count == 1),
                It.IsAny<NachaCreditFileOptions>()))
            .Returns(new NachaCreditFileResult { FileReference = "NACHA-CR-TEST" });

        await _service.GenerateNachaCreditFileAsync();

        _nachaService.Verify(s => s.GenerateNachaCreditFile(
            It.Is<List<NachaCreditEntryDetail>>(e => e.Count == 1),
            It.IsAny<NachaCreditFileOptions>()), Times.Once);
    }

    #endregion

    #region ProcessStripeWebhookAsync

    [Fact]
    public async Task ProcessStripeWebhookAsync_PayoutPaid_SettlesDisbursement()
    {
        var disbursement = new CapitationDisbursement
        {
            Id = "disb-1", StatementId = "stmt-1", StatementNumber = "CAPSTMT-001",
            Status = DisbursementStatus.Submitted, Amount = 5000,
            StripeTransferId = "tr_abc123", Method = DisbursementMethod.StripeConnect
        };
        _disbursementRepo.Setup(r => r.GetByStripeTransferIdAsync("tr_abc123"))
            .ReturnsAsync(new[] { disbursement });
        _disbursementRepo.Setup(r => r.GetByIdAsync("disb-1")).ReturnsAsync(disbursement);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        var statement = CreateApprovedStatement();
        statement.Status = CapitationStatementStatus.PaymentInitiated;
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);

        _stripeService.Setup(s => s.ProcessWebhookAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new DisbursementWebhookResult
            {
                Handled = true,
                EventType = "payout_paid",
                TransferId = "tr_abc123"
            });

        await _service.ProcessStripeWebhookAsync("{}", "sig_test");

        _disbursementRepo.Verify(r => r.UpdateAsync(It.Is<CapitationDisbursement>(d =>
            d.Status == DisbursementStatus.Settled)), Times.Once);
    }

    [Fact]
    public async Task ProcessStripeWebhookAsync_TransferReversed_ProcessesReturn()
    {
        var disbursement = new CapitationDisbursement
        {
            Id = "disb-1", StatementId = "stmt-1",
            Status = DisbursementStatus.Submitted, Amount = 5000,
            StripeTransferId = "tr_abc123"
        };
        _disbursementRepo.Setup(r => r.GetByStripeTransferIdAsync("tr_abc123"))
            .ReturnsAsync(new[] { disbursement });
        _disbursementRepo.Setup(r => r.GetByIdAsync("disb-1")).ReturnsAsync(disbursement);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        var statement = CreateApprovedStatement();
        statement.Status = CapitationStatementStatus.PaymentInitiated;
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);

        _stripeService.Setup(s => s.ProcessWebhookAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new DisbursementWebhookResult
            {
                Handled = true,
                EventType = "transfer_reversed",
                TransferId = "tr_abc123",
                FailureCode = "TRANSFER_REVERSED",
                FailureMessage = "Transfer was reversed"
            });

        await _service.ProcessStripeWebhookAsync("{}", "sig_test");

        _disbursementRepo.Verify(r => r.UpdateAsync(It.Is<CapitationDisbursement>(d =>
            d.Status == DisbursementStatus.Returned)), Times.Once);
    }

    [Fact]
    public async Task ProcessStripeWebhookAsync_UnhandledEvent_DoesNothing()
    {
        _stripeService.Setup(s => s.ProcessWebhookAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new DisbursementWebhookResult { Handled = false });

        await _service.ProcessStripeWebhookAsync("{}", "sig_test");

        _disbursementRepo.Verify(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()), Times.Never);
    }

    [Fact]
    public async Task ProcessStripeWebhookAsync_NoDisbursementFound_DoesNothing()
    {
        _stripeService.Setup(s => s.ProcessWebhookAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new DisbursementWebhookResult
            {
                Handled = true,
                EventType = "payout_paid",
                TransferId = "tr_unknown"
            });
        _disbursementRepo.Setup(r => r.GetByStripeTransferIdAsync("tr_unknown"))
            .ReturnsAsync(Enumerable.Empty<CapitationDisbursement>());

        await _service.ProcessStripeWebhookAsync("{}", "sig_test");

        _disbursementRepo.Verify(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()), Times.Never);
    }

    #endregion

    #region InitiateDisbursementAsync edge cases

    [Fact]
    public async Task InitiateDisbursementAsync_EftDisabled_Throws()
    {
        var statement = CreateApprovedStatement();
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        SetupProviderBankAccountResponse(new ProviderBankAccountDto { EftEnabled = false });

        var act = () => _service.InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "stmt-1"
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*EFT not enabled*");
    }

    [Fact]
    public async Task InitiateDisbursementAsync_ZeroNetPayable_Throws()
    {
        var statement = CreateApprovedStatement(netPayable: 0);
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);

        var act = () => _service.InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "stmt-1"
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no net payable*");
    }

    [Fact]
    public async Task InitiateDisbursementAsync_MissingStatement_Throws()
    {
        _statementRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((CapitationStatement?)null);

        var act = () => _service.InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "missing"
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task InitiateDisbursementAsync_StripeConnectFailed_SetsFailedStatus()
    {
        var statement = CreateApprovedStatement();
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);
        SetupProviderBankAccountResponse(new ProviderBankAccountDto
        {
            EftEnabled = true,
            PreferredDisbursementMethod = "StripeConnect",
            StripeConnectedAccountId = "acct_test",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "4321"
        });
        _stripeService.Setup(s => s.CreateTransferAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new StripeTransferResult
            {
                Status = "failed",
                ErrorMessage = "Account not connected"
            });
        _disbursementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        var result = await _service.InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "stmt-1",
            Method = DisbursementMethod.StripeConnect
        });

        result.Status.Should().Be(DisbursementStatus.Failed);
        result.ErrorMessage.Should().Be("Account not connected");
    }

    [Fact]
    public async Task InitiateDisbursementAsync_CheckMethod_MarksSubmitted()
    {
        var statement = CreateApprovedStatement();
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);
        SetupProviderBankAccountResponse(new ProviderBankAccountDto
        {
            EftEnabled = true,
            PreferredDisbursementMethod = "Check",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "4321"
        });
        _disbursementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        var result = await _service.InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "stmt-1",
            Method = DisbursementMethod.Check
        });

        result.Method.Should().Be(DisbursementMethod.Check);
        result.Status.Should().Be(DisbursementStatus.Submitted);
        result.SubmittedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task InitiateBatchDisbursementAsync_WithCapitationRunId_IncludesRunStatements()
    {
        var run = new CapitationRun { Id = "run-1", StatementIds = new List<string> { "stmt-from-run" } };
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);

        var statement = CreateApprovedStatement("stmt-from-run");
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-from-run")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);
        SetupProviderBankAccountResponse(new ProviderBankAccountDto
        {
            EftEnabled = true,
            PreferredDisbursementMethod = "NachaCredit",
            RoutingNumber = "091000019",
            AccountNumber = "123456789",
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "6789"
        });
        _disbursementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);
        _nachaService.Setup(s => s.GenerateNachaCreditFile(
                It.IsAny<List<NachaCreditEntryDetail>>(), It.IsAny<NachaCreditFileOptions>()))
            .Returns(new NachaCreditFileResult { FileReference = "NACHA-CR-TEST" });

        var result = await _service.InitiateBatchDisbursementAsync(new InitiateBatchDisbursementRequest
        {
            CapitationRunId = "run-1",
            InitiatedBy = "admin"
        });

        result.DisbursementsInitiated.Should().Be(1);
        result.TotalStatements.Should().Be(1);
    }

    [Fact]
    public async Task InitiateBatchDisbursementAsync_SkipsNonApproved()
    {
        var stmt1 = CreateApprovedStatement("stmt-ok");
        var stmt2 = CreateApprovedStatement("stmt-gen");
        stmt2.Status = CapitationStatementStatus.Generated; // Not approved

        _statementRepo.Setup(r => r.GetByIdAsync("stmt-ok")).ReturnsAsync(stmt1);
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-gen")).ReturnsAsync(stmt2);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);
        SetupProviderBankAccountResponse(new ProviderBankAccountDto
        {
            EftEnabled = true, PreferredDisbursementMethod = "NachaCredit",
            RoutingNumber = "091000019", AccountNumber = "123456789",
            RoutingNumberLast4 = "0019", AccountNumberLast4 = "6789"
        });
        _disbursementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);
        _nachaService.Setup(s => s.GenerateNachaCreditFile(
                It.IsAny<List<NachaCreditEntryDetail>>(), It.IsAny<NachaCreditFileOptions>()))
            .Returns(new NachaCreditFileResult { FileReference = "NACHA-CR-TEST" });

        var result = await _service.InitiateBatchDisbursementAsync(new InitiateBatchDisbursementRequest
        {
            StatementIds = new List<string> { "stmt-ok", "stmt-gen" }
        });

        result.DisbursementsInitiated.Should().Be(1);
        result.Skipped.Should().Be(1);
    }

    [Fact]
    public async Task SettleDisbursementAsync_CheckMethod_SetsCheckNumber()
    {
        var disbursement = new CapitationDisbursement
        {
            Id = "disb-1", StatementId = "stmt-1",
            Status = DisbursementStatus.Submitted, Amount = 5000,
            Method = DisbursementMethod.Check, CheckNumber = "CHK-99999"
        };
        _disbursementRepo.Setup(r => r.GetByIdAsync("disb-1")).ReturnsAsync(disbursement);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);

        var statement = CreateApprovedStatement();
        statement.Status = CapitationStatementStatus.PaymentInitiated;
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        CapitationStatement? saved = null;
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => saved = s)
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.SettleDisbursementAsync("disb-1");

        saved!.CheckNumber.Should().Be("CHK-99999");
        saved.Status.Should().Be(CapitationStatementStatus.Paid);
    }

    [Fact]
    public async Task ProcessReturnAsync_NonRetryableCode_DoesNotFlagRetry()
    {
        var disbursement = new CapitationDisbursement
        {
            Id = "disb-1", StatementId = "stmt-1",
            Status = DisbursementStatus.Submitted, Amount = 5000,
            RetryCount = 0, MaxRetries = 2
        };
        _disbursementRepo.Setup(r => r.GetByIdAsync("disb-1")).ReturnsAsync(disbursement);
        _disbursementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(CreateApprovedStatement());
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);

        var result = await _service.ProcessReturnAsync(new ProcessReturnRequest
        {
            DisbursementId = "disb-1",
            ReturnCode = "R02" // Account closed — non-retryable
        });

        result.ReturnCode.Should().Be("R02");
        result.ReturnReason.Should().Be("Account Closed");
    }

    #endregion
}
