using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using CapitationService.Services;

namespace CapitationService.Tests.Services;

public class StripeConnectServiceTests
{
    private readonly Mock<IStripeTransferClient> _stripeClient;
    private readonly StripeConnectService _service;

    public StripeConnectServiceTests()
    {
        _stripeClient = new Mock<IStripeTransferClient>();

        var configData = new Dictionary<string, string?>
        {
            { "Stripe:SecretKey", "sk_test_fake" },
            { "Stripe:ConnectWebhookSecret", "whsec_test_fake" }
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var logger = new Mock<ILogger<StripeConnectService>>();

        _service = new StripeConnectService(_stripeClient.Object, configuration, logger.Object);
    }

    private static Transfer CreateFakeTransfer(
        string id = "tr_abc123",
        long amountCents = 500000,
        bool reversed = false,
        string? statementNumber = "CAPSTMT-123-2026-03") => new()
    {
        Id = id,
        Amount = amountCents,
        Reversed = reversed,
        Created = new DateTime(2026, 3, 15),
        Metadata = new Dictionary<string, string>
        {
            { "statement_number", statementNumber ?? "" },
            { "provider_npi", "1234567890" },
            { "type", "capitation" }
        }
    };

    private static Payout CreateFakePayout(
        string id = "po_xyz789",
        long amountCents = 500000,
        string? failureCode = null,
        string? failureMessage = null) => new()
    {
        Id = id,
        Amount = amountCents,
        FailureCode = failureCode,
        FailureMessage = failureMessage
    };

    #region CreateTransferAsync

    [Fact]
    public async Task CreateTransferAsync_Success_ReturnsResult()
    {
        var transfer = CreateFakeTransfer();
        _stripeClient.Setup(c => c.CreateTransferAsync(It.IsAny<TransferCreateOptions>()))
            .ReturnsAsync(transfer);

        var result = await _service.CreateTransferAsync("acct_test", 5000.00m, "CAPSTMT-123", "1234567890");

        result.TransferId.Should().Be("tr_abc123");
        result.Status.Should().Be("created");
        result.Amount.Should().Be(5000.00m);
        result.StatementNumber.Should().Be("CAPSTMT-123-2026-03");
    }

    [Fact]
    public async Task CreateTransferAsync_ConvertsAmountToCents()
    {
        TransferCreateOptions? capturedOptions = null;
        _stripeClient.Setup(c => c.CreateTransferAsync(It.IsAny<TransferCreateOptions>()))
            .Callback<TransferCreateOptions>(o => capturedOptions = o)
            .ReturnsAsync(CreateFakeTransfer());

        await _service.CreateTransferAsync("acct_test", 1234.56m, "STMT-1", "1234567890");

        capturedOptions.Should().NotBeNull();
        capturedOptions!.Amount.Should().Be(123456); // cents
        capturedOptions.Currency.Should().Be("usd");
        capturedOptions.Destination.Should().Be("acct_test");
    }

    [Fact]
    public async Task CreateTransferAsync_SetsCapitationMetadata()
    {
        TransferCreateOptions? capturedOptions = null;
        _stripeClient.Setup(c => c.CreateTransferAsync(It.IsAny<TransferCreateOptions>()))
            .Callback<TransferCreateOptions>(o => capturedOptions = o)
            .ReturnsAsync(CreateFakeTransfer());

        await _service.CreateTransferAsync("acct_test", 100m, "STMT-99", "5551234567");

        capturedOptions!.Metadata.Should().ContainKey("statement_number").WhoseValue.Should().Be("STMT-99");
        capturedOptions.Metadata.Should().ContainKey("provider_npi").WhoseValue.Should().Be("5551234567");
        capturedOptions.Metadata.Should().ContainKey("type").WhoseValue.Should().Be("capitation");
    }

    [Fact]
    public async Task CreateTransferAsync_StripeError_ReturnsFailedResult()
    {
        _stripeClient.Setup(c => c.CreateTransferAsync(It.IsAny<TransferCreateOptions>()))
            .ThrowsAsync(new StripeException("Account not connected"));

        var result = await _service.CreateTransferAsync("acct_bad", 100m, "STMT-1", "1234567890");

        result.Status.Should().Be("failed");
        result.ErrorMessage.Should().Be("Account not connected");
    }

    [Fact]
    public async Task CreateTransferAsync_ReversedTransfer_ReturnsReversedStatus()
    {
        var transfer = CreateFakeTransfer(reversed: true);
        _stripeClient.Setup(c => c.CreateTransferAsync(It.IsAny<TransferCreateOptions>()))
            .ReturnsAsync(transfer);

        var result = await _service.CreateTransferAsync("acct_test", 100m, "STMT-1", "1234567890");

        result.Status.Should().Be("reversed");
    }

    #endregion

    #region GetTransferStatusAsync

    [Fact]
    public async Task GetTransferStatusAsync_Success_ReturnsResult()
    {
        _stripeClient.Setup(c => c.GetTransferAsync("tr_abc123"))
            .ReturnsAsync(CreateFakeTransfer());

        var result = await _service.GetTransferStatusAsync("tr_abc123");

        result.TransferId.Should().Be("tr_abc123");
        result.Amount.Should().Be(5000.00m);
    }

    [Fact]
    public async Task GetTransferStatusAsync_StripeError_Throws()
    {
        _stripeClient.Setup(c => c.GetTransferAsync("tr_bad"))
            .ThrowsAsync(new StripeException("Transfer not found"));

        var act = () => _service.GetTransferStatusAsync("tr_bad");

        await act.Should().ThrowAsync<StripeException>();
    }

    #endregion

    #region CancelTransferAsync

    [Fact]
    public async Task CancelTransferAsync_CreatesReversal()
    {
        _stripeClient.Setup(c => c.CreateTransferReversalAsync("tr_abc123"))
            .Returns(Task.CompletedTask);

        await _service.CancelTransferAsync("tr_abc123");

        _stripeClient.Verify(c => c.CreateTransferReversalAsync("tr_abc123"), Times.Once);
    }

    [Fact]
    public async Task CancelTransferAsync_StripeError_Throws()
    {
        _stripeClient.Setup(c => c.CreateTransferReversalAsync("tr_bad"))
            .ThrowsAsync(new StripeException("Cannot reverse"));

        var act = () => _service.CancelTransferAsync("tr_bad");

        await act.Should().ThrowAsync<StripeException>();
    }

    #endregion

    #region ProcessWebhookAsync

    [Fact]
    public async Task ProcessWebhookAsync_TransferCreated_ReturnsSubmitted()
    {
        var transfer = CreateFakeTransfer("tr_new", 300000);
        var stripeEvent = new Event
        {
            Type = "transfer.created",
            Data = new EventData { Object = transfer }
        };
        _stripeClient.Setup(c => c.ConstructWebhookEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(stripeEvent);

        var result = await _service.ProcessWebhookAsync("{}", "sig_test");

        result.Handled.Should().BeTrue();
        result.EventType.Should().Be("transfer_created");
        result.TransferId.Should().Be("tr_new");
        result.Amount.Should().Be(3000.00m);
        result.Status.Should().Be("submitted");
    }

    [Fact]
    public async Task ProcessWebhookAsync_TransferReversed_ReturnsReturned()
    {
        var transfer = CreateFakeTransfer("tr_rev", 200000);
        var stripeEvent = new Event
        {
            Type = "transfer.reversed",
            Data = new EventData { Object = transfer }
        };
        _stripeClient.Setup(c => c.ConstructWebhookEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(stripeEvent);

        var result = await _service.ProcessWebhookAsync("{}", "sig_test");

        result.Handled.Should().BeTrue();
        result.EventType.Should().Be("transfer_reversed");
        result.Status.Should().Be("returned");
        result.FailureCode.Should().Be("TRANSFER_REVERSED");
    }

    [Fact]
    public async Task ProcessWebhookAsync_PayoutPaid_ReturnsSettled()
    {
        var payout = CreateFakePayout("po_paid", 500000);
        var stripeEvent = new Event
        {
            Type = "payout.paid",
            Data = new EventData { Object = payout }
        };
        _stripeClient.Setup(c => c.ConstructWebhookEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(stripeEvent);

        var result = await _service.ProcessWebhookAsync("{}", "sig_test");

        result.Handled.Should().BeTrue();
        result.EventType.Should().Be("payout_paid");
        result.TransferId.Should().Be("po_paid");
        result.Amount.Should().Be(5000.00m);
        result.Status.Should().Be("settled");
    }

    [Fact]
    public async Task ProcessWebhookAsync_PayoutFailed_ReturnsFailed()
    {
        var payout = CreateFakePayout("po_fail", 100000, "account_closed", "Bank account closed");
        var stripeEvent = new Event
        {
            Type = "payout.failed",
            Data = new EventData { Object = payout }
        };
        _stripeClient.Setup(c => c.ConstructWebhookEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(stripeEvent);

        var result = await _service.ProcessWebhookAsync("{}", "sig_test");

        result.Handled.Should().BeTrue();
        result.EventType.Should().Be("payout_failed");
        result.Status.Should().Be("failed");
        result.FailureCode.Should().Be("account_closed");
        result.FailureMessage.Should().Be("Bank account closed");
    }

    [Fact]
    public async Task ProcessWebhookAsync_UnhandledEventType_ReturnsNotHandled()
    {
        var stripeEvent = new Event
        {
            Type = "charge.succeeded",
            Data = new EventData()
        };
        _stripeClient.Setup(c => c.ConstructWebhookEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(stripeEvent);

        var result = await _service.ProcessWebhookAsync("{}", "sig_test");

        result.Handled.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessWebhookAsync_TransferCreated_NullObject_ReturnsNotHandled()
    {
        var stripeEvent = new Event
        {
            Type = "transfer.created",
            Data = new EventData { Object = new Charge() } // Wrong type, cast to Transfer will be null
        };
        _stripeClient.Setup(c => c.ConstructWebhookEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(stripeEvent);

        var result = await _service.ProcessWebhookAsync("{}", "sig_test");

        result.Handled.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessWebhookAsync_StripeException_Throws()
    {
        _stripeClient.Setup(c => c.ConstructWebhookEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new StripeException("Invalid signature"));

        var act = () => _service.ProcessWebhookAsync("{}", "bad_sig");

        await act.Should().ThrowAsync<StripeException>();
    }

    #endregion
}
