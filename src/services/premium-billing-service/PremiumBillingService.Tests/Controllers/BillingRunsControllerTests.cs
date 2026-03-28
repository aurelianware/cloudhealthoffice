using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using PremiumBillingService.Controllers;
using PremiumBillingService.Models;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Controllers;

public class BillingRunsControllerTests
{
    private readonly Mock<IPremiumBillingService> _billingService;
    private readonly BillingRunsController _controller;

    public BillingRunsControllerTests()
    {
        _billingService = new Mock<IPremiumBillingService>();
        var logger = new Mock<ILogger<BillingRunsController>>();
        _controller = new BillingRunsController(_billingService.Object, logger.Object);
    }

    #region CreateBillingRun

    [Fact]
    public async Task CreateBillingRun_ValidRequest_Returns201WithBillingRun()
    {
        var request = new CreateBillingRunRequest
        {
            BillingPeriod = new DateTime(2026, 3, 1),
            CreatedBy = "admin",
            Description = "March billing"
        };
        var billingRun = new BillingRun { Id = "br-1", BillingRunNumber = "BR-2026-03-ABCD" };
        _billingService.Setup(s => s.CreateBillingRunAsync(request, request.CreatedBy))
            .ReturnsAsync(billingRun);

        var result = await _controller.CreateBillingRun(request);

        var createdResult = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(201);
        createdResult.ActionName.Should().Be(nameof(BillingRunsController.GetBillingRunById));
        var returned = createdResult.Value.Should().BeOfType<BillingRun>().Subject;
        returned.Id.Should().Be("br-1");
    }

    #endregion

    #region ExecuteBillingRun

    [Fact]
    public async Task ExecuteBillingRun_Success_Returns200()
    {
        var billingRun = new BillingRun { Id = "br-1", Status = BillingRunStatus.Completed };
        _billingService.Setup(s => s.ExecuteBillingRunAsync("br-1"))
            .ReturnsAsync(billingRun);

        var result = await _controller.ExecuteBillingRun("br-1");

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<BillingRun>().Subject;
        returned.Status.Should().Be(BillingRunStatus.Completed);
    }

    [Fact]
    public async Task ExecuteBillingRun_InvalidOperation_Returns400()
    {
        _billingService.Setup(s => s.ExecuteBillingRunAsync("br-1"))
            .ThrowsAsync(new InvalidOperationException("Not in Pending state"));

        var result = await _controller.ExecuteBillingRun("br-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region CreateAndExecuteBillingRun

    [Fact]
    public async Task CreateAndExecuteBillingRun_Success_Returns200()
    {
        var request = new CreateBillingRunRequest
        {
            BillingPeriod = new DateTime(2026, 3, 1),
            CreatedBy = "admin"
        };
        var created = new BillingRun { Id = "br-1", Status = BillingRunStatus.Pending };
        var executed = new BillingRun { Id = "br-1", Status = BillingRunStatus.Completed, TotalInvoices = 5 };

        _billingService.Setup(s => s.CreateBillingRunAsync(request, request.CreatedBy))
            .ReturnsAsync(created);
        _billingService.Setup(s => s.ExecuteBillingRunAsync("br-1"))
            .ReturnsAsync(executed);

        var result = await _controller.CreateAndExecuteBillingRun(request);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<BillingRun>().Subject;
        returned.TotalInvoices.Should().Be(5);
    }

    #endregion

    #region GetBillingRunById

    [Fact]
    public async Task GetBillingRunById_Found_Returns200()
    {
        var billingRun = new BillingRun { Id = "br-1", BillingRunNumber = "BR-2026-03-ABCD" };
        _billingService.Setup(s => s.GetBillingRunAsync("br-1"))
            .ReturnsAsync(billingRun);

        var result = await _controller.GetBillingRunById("br-1");

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<BillingRun>().Subject;
        returned.BillingRunNumber.Should().Be("BR-2026-03-ABCD");
    }

    [Fact]
    public async Task GetBillingRunById_NotFound_Returns404()
    {
        _billingService.Setup(s => s.GetBillingRunAsync("missing"))
            .ThrowsAsync(new InvalidOperationException("Not found"));

        var result = await _controller.GetBillingRunById("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region GetBillingRuns

    [Fact]
    public async Task GetBillingRuns_WithDateRange_Returns200()
    {
        var from = new DateTime(2026, 1, 1);
        var to = new DateTime(2026, 3, 31);
        var runs = new List<BillingRun>
        {
            new() { Id = "br-1" },
            new() { Id = "br-2" }
        };
        _billingService.Setup(s => s.GetBillingRunsAsync(from, to))
            .ReturnsAsync(runs);

        var result = await _controller.GetBillingRuns(from, to);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeAssignableTo<IEnumerable<BillingRun>>().Subject;
        returned.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetBillingRuns_NoFilters_Returns200()
    {
        _billingService.Setup(s => s.GetBillingRunsAsync(null, null))
            .ReturnsAsync(new List<BillingRun>());

        var result = await _controller.GetBillingRuns(null, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    #endregion

    #region CancelBillingRun

    [Fact]
    public async Task CancelBillingRun_Success_Returns204()
    {
        _billingService.Setup(s => s.CancelBillingRunAsync("br-1"))
            .Returns(Task.CompletedTask);

        var result = await _controller.CancelBillingRun("br-1");

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task CancelBillingRun_NotPending_Returns400()
    {
        _billingService.Setup(s => s.CancelBillingRunAsync("br-1"))
            .ThrowsAsync(new InvalidOperationException("Not in Pending state"));

        var result = await _controller.CancelBillingRun("br-1");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion
}
