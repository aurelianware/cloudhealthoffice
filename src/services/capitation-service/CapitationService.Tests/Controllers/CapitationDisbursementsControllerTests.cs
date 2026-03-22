using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using CapitationService.Controllers;
using CapitationService.Models;
using CapitationService.Services;

namespace CapitationService.Tests.Controllers;

public class CapitationDisbursementsControllerTests
{
    private readonly Mock<ICapitationDisbursementService> _disbursementService;
    private readonly CapitationDisbursementsController _controller;

    public CapitationDisbursementsControllerTests()
    {
        _disbursementService = new Mock<ICapitationDisbursementService>();
        var logger = new Mock<ILogger<CapitationDisbursementsController>>();
        _controller = new CapitationDisbursementsController(_disbursementService.Object, logger.Object);
    }

    private static CapitationDisbursement CreateDisbursement(
        string id = "disb-1",
        DisbursementStatus status = DisbursementStatus.Pending) => new()
    {
        Id = id,
        StatementId = "stmt-1",
        StatementNumber = "CAPSTMT-123-2026-03",
        ProviderNPI = "1234567890",
        ProviderName = "Dr. Chen",
        Amount = 5000m,
        Method = DisbursementMethod.NachaCredit,
        Status = status
    };

    #region InitiateDisbursement

    [Fact]
    public async Task InitiateDisbursement_Success_ReturnsCreated()
    {
        var disbursement = CreateDisbursement();
        _disbursementService.Setup(s => s.InitiateDisbursementAsync(It.IsAny<InitiateDisbursementRequest>()))
            .ReturnsAsync(disbursement);

        var result = await _controller.InitiateDisbursement(
            new InitiateDisbursementRequest { StatementId = "stmt-1" });

        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        created!.StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task InitiateDisbursement_InvalidState_ReturnsBadRequest()
    {
        _disbursementService.Setup(s => s.InitiateDisbursementAsync(It.IsAny<InitiateDisbursementRequest>()))
            .ThrowsAsync(new InvalidOperationException("Statement not approved"));

        var result = await _controller.InitiateDisbursement(
            new InitiateDisbursementRequest { StatementId = "stmt-1" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region InitiateBatchDisbursement

    [Fact]
    public async Task InitiateBatchDisbursement_Success_ReturnsOk()
    {
        var batchResult = new BatchDisbursementResult
        {
            TotalStatements = 3,
            DisbursementsInitiated = 2,
            Skipped = 1,
            TotalAmount = 10000m
        };
        _disbursementService.Setup(s => s.InitiateBatchDisbursementAsync(It.IsAny<InitiateBatchDisbursementRequest>()))
            .ReturnsAsync(batchResult);

        var result = await _controller.InitiateBatchDisbursement(
            new InitiateBatchDisbursementRequest { StatementIds = new List<string> { "s1", "s2", "s3" } });

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as BatchDisbursementResult)!.DisbursementsInitiated.Should().Be(2);
    }

    #endregion

    #region GenerateNachaCreditFile

    [Fact]
    public async Task GenerateNachaCreditFile_Success_ReturnsOk()
    {
        var nachaResult = new NachaCreditFileResult
        {
            FileReference = "NACHA-CR-TEST",
            EntryCount = 5,
            TotalAmount = 25000m
        };
        _disbursementService.Setup(s => s.GenerateNachaCreditFileAsync()).ReturnsAsync(nachaResult);

        var result = await _controller.GenerateNachaCreditFile();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as NachaCreditFileResult)!.FileReference.Should().Be("NACHA-CR-TEST");
    }

    [Fact]
    public async Task GenerateNachaCreditFile_NoPending_ReturnsBadRequest()
    {
        _disbursementService.Setup(s => s.GenerateNachaCreditFileAsync())
            .ThrowsAsync(new InvalidOperationException("No pending NACHA disbursements"));

        var result = await _controller.GenerateNachaCreditFile();

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region GetDisbursement

    [Fact]
    public async Task GetDisbursementById_Found_ReturnsOk()
    {
        _disbursementService.Setup(s => s.GetDisbursementByIdAsync("disb-1"))
            .ReturnsAsync(CreateDisbursement());

        var result = await _controller.GetDisbursementById("disb-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CapitationDisbursement)!.Amount.Should().Be(5000m);
    }

    [Fact]
    public async Task GetDisbursementById_NotFound_Returns404()
    {
        _disbursementService.Setup(s => s.GetDisbursementByIdAsync("missing"))
            .ReturnsAsync((CapitationDisbursement?)null);

        var result = await _controller.GetDisbursementById("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetDisbursementsByStatement_ReturnsOk()
    {
        var disbursements = new List<CapitationDisbursement>
        {
            CreateDisbursement("d1"), CreateDisbursement("d2")
        };
        _disbursementService.Setup(s => s.GetDisbursementsByStatementAsync("stmt-1"))
            .ReturnsAsync(disbursements);

        var result = await _controller.GetDisbursementsByStatement("stmt-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<CapitationDisbursement>)!.Should().HaveCount(2);
    }

    #endregion

    #region CancelDisbursement

    [Fact]
    public async Task CancelDisbursement_Pending_ReturnsOk()
    {
        var cancelled = CreateDisbursement(status: DisbursementStatus.Cancelled);
        _disbursementService.Setup(s => s.CancelDisbursementAsync("disb-1")).ReturnsAsync(cancelled);

        var result = await _controller.CancelDisbursement("disb-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CapitationDisbursement)!.Status.Should().Be(DisbursementStatus.Cancelled);
    }

    [Fact]
    public async Task CancelDisbursement_NotPending_ReturnsBadRequest()
    {
        _disbursementService.Setup(s => s.CancelDisbursementAsync("disb-1"))
            .ThrowsAsync(new InvalidOperationException("Can only cancel Pending"));

        var result = await _controller.CancelDisbursement("disb-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region ProcessReturn

    [Fact]
    public async Task ProcessReturn_Success_ReturnsOk()
    {
        var returned = CreateDisbursement(status: DisbursementStatus.Returned);
        returned.ReturnCode = "R01";
        returned.ReturnReason = "Insufficient Funds";
        _disbursementService.Setup(s => s.ProcessReturnAsync(It.IsAny<ProcessReturnRequest>()))
            .ReturnsAsync(returned);

        var result = await _controller.ProcessReturn(
            new ProcessReturnRequest { DisbursementId = "disb-1", ReturnCode = "R01" });

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CapitationDisbursement)!.ReturnCode.Should().Be("R01");
    }

    [Fact]
    public async Task ProcessReturn_InvalidState_ReturnsBadRequest()
    {
        _disbursementService.Setup(s => s.ProcessReturnAsync(It.IsAny<ProcessReturnRequest>()))
            .ThrowsAsync(new InvalidOperationException("Cannot process return for Pending"));

        var result = await _controller.ProcessReturn(
            new ProcessReturnRequest { DisbursementId = "disb-1", ReturnCode = "R01" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region BatchDisbursement error path

    [Fact]
    public async Task InitiateBatchDisbursement_InvalidState_ReturnsBadRequest()
    {
        _disbursementService.Setup(s => s.InitiateBatchDisbursementAsync(It.IsAny<InitiateBatchDisbursementRequest>()))
            .ThrowsAsync(new InvalidOperationException("Run not found"));

        var result = await _controller.InitiateBatchDisbursement(
            new InitiateBatchDisbursementRequest { CapitationRunId = "missing" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion
}
