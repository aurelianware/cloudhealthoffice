using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using PremiumBillingService.Controllers;
using PremiumBillingService.Models;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Controllers;

public class EftControllerTests
{
    private readonly Mock<IEftDraftService> _draftService;
    private readonly EftController _controller;

    public EftControllerTests()
    {
        _draftService = new Mock<IEftDraftService>();
        var logger = new Mock<ILogger<EftController>>();
        _controller = new EftController(_draftService.Object, logger.Object);
    }

    #region InitiateDraft

    [Fact]
    public async Task InitiateDraft_ValidRequest_Returns201()
    {
        var draft = new EftDraft { Id = "d1", InvoiceId = "inv-1", Amount = 1000 };
        _draftService.Setup(s => s.InitiateDraftAsync(It.IsAny<InitiateEftDraftRequest>()))
            .ReturnsAsync(draft);

        var result = await _controller.InitiateDraft(new InitiateEftDraftRequest { InvoiceId = "inv-1" });

        var createdResult = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(201);
        var returnedDraft = createdResult.Value.Should().BeOfType<EftDraft>().Subject;
        returnedDraft.Id.Should().Be("d1");
    }

    [Fact]
    public async Task InitiateDraft_InvalidOperation_Returns400()
    {
        _draftService.Setup(s => s.InitiateDraftAsync(It.IsAny<InitiateEftDraftRequest>()))
            .ThrowsAsync(new InvalidOperationException("Invoice not found"));

        var result = await _controller.InitiateDraft(new InitiateEftDraftRequest { InvoiceId = "bad" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region InitiateBatchDraft

    [Fact]
    public async Task InitiateBatchDraft_ValidRequest_Returns200()
    {
        var batchResult = new BatchEftResult { DraftsInitiated = 5, TotalAmount = 7500 };
        _draftService.Setup(s => s.InitiateBatchDraftAsync(It.IsAny<InitiateBatchEftRequest>()))
            .ReturnsAsync(batchResult);

        var result = await _controller.InitiateBatchDraft(new InitiateBatchEftRequest
        {
            InvoiceIds = new List<string> { "inv-1", "inv-2" }
        });

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedResult = okResult.Value.Should().BeOfType<BatchEftResult>().Subject;
        returnedResult.DraftsInitiated.Should().Be(5);
    }

    #endregion

    #region GenerateNachaFile

    [Fact]
    public async Task GenerateNachaFile_Success_ReturnsNachaResult()
    {
        var nachaResult = new NachaFileResult
        {
            FileReference = "NACHA-123",
            EntryCount = 10,
            TotalAmount = 15000
        };
        _draftService.Setup(s => s.GenerateNachaFileForPendingDraftsAsync())
            .ReturnsAsync(nachaResult);

        var result = await _controller.GenerateNachaFile();

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<NachaFileResult>().Subject;
        returned.FileReference.Should().Be("NACHA-123");
    }

    [Fact]
    public async Task GenerateNachaFile_NoPending_Returns400()
    {
        _draftService.Setup(s => s.GenerateNachaFileForPendingDraftsAsync())
            .ThrowsAsync(new InvalidOperationException("No pending NACHA drafts"));

        var result = await _controller.GenerateNachaFile();

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region GenerateAndDownloadNachaFile

    [Fact]
    public async Task GenerateAndDownloadNachaFile_Success_ReturnsFile()
    {
        var nachaResult = new NachaFileResult
        {
            FileContent = "101 091000019...",
            FileName = "ACH-2026-03.ach"
        };
        _draftService.Setup(s => s.GenerateNachaFileForPendingDraftsAsync())
            .ReturnsAsync(nachaResult);

        var result = await _controller.GenerateAndDownloadNachaFile();

        var fileResult = result.Should().BeOfType<FileContentResult>().Subject;
        fileResult.ContentType.Should().Be("text/plain");
        fileResult.FileDownloadName.Should().Be("ACH-2026-03.ach");
    }

    #endregion

    #region GetDraftById

    [Fact]
    public async Task GetDraftById_Found_Returns200()
    {
        var draft = new EftDraft { Id = "d1", Amount = 1000 };
        _draftService.Setup(s => s.GetDraftByIdAsync("d1")).ReturnsAsync(draft);

        var result = await _controller.GetDraftById("d1");

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<EftDraft>().Subject;
        returned.Id.Should().Be("d1");
    }

    [Fact]
    public async Task GetDraftById_NotFound_Returns404()
    {
        _draftService.Setup(s => s.GetDraftByIdAsync("missing")).ReturnsAsync((EftDraft?)null);

        var result = await _controller.GetDraftById("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region SettleDraft

    [Fact]
    public async Task SettleDraft_Success_Returns200()
    {
        var draft = new EftDraft { Id = "d1", Status = EftDraftStatus.Settled };
        _draftService.Setup(s => s.SettleDraftAsync("d1")).ReturnsAsync(draft);

        var result = await _controller.SettleDraft("d1");

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<EftDraft>().Subject;
        returned.Status.Should().Be(EftDraftStatus.Settled);
    }

    #endregion

    #region CancelDraft

    [Fact]
    public async Task CancelDraft_Success_Returns200()
    {
        var draft = new EftDraft { Id = "d1", Status = EftDraftStatus.Cancelled };
        _draftService.Setup(s => s.CancelDraftAsync("d1")).ReturnsAsync(draft);

        var result = await _controller.CancelDraft("d1");

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<EftDraft>().Subject;
        returned.Status.Should().Be(EftDraftStatus.Cancelled);
    }

    [Fact]
    public async Task CancelDraft_NotPending_Returns400()
    {
        _draftService.Setup(s => s.CancelDraftAsync("d1"))
            .ThrowsAsync(new InvalidOperationException("Can only cancel Pending drafts"));

        var result = await _controller.CancelDraft("d1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region StripeWebhook

    [Fact]
    public async Task StripeWebhook_MissingSignature_Returns400()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await _controller.StripeWebhook();

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion
}
