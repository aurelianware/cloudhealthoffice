using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using CapitationService.Controllers;
using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;

namespace CapitationService.Tests.Controllers;

public class CapitationRunsControllerTests
{
    private readonly Mock<ICapitationRunService> _runService;
    private readonly Mock<ICapitationStatementRepository> _statementRepo;
    private readonly CapitationRunsController _controller;

    public CapitationRunsControllerTests()
    {
        _runService = new Mock<ICapitationRunService>();
        _statementRepo = new Mock<ICapitationStatementRepository>();
        var logger = new Mock<ILogger<CapitationRunsController>>();

        _controller = new CapitationRunsController(
            _runService.Object,
            _statementRepo.Object,
            logger.Object);
    }

    [Fact]
    public async Task CreateRun_ReturnsCreatedAtAction()
    {
        var run = new CapitationRun
        {
            Id = "run-1",
            RunNumber = "CAPRUN-2026-03-ABCD",
            Status = CapitationRunStatus.Pending
        };
        _runService.Setup(s => s.CreateRunAsync(It.IsAny<CreateCapitationRunRequest>(), It.IsAny<string>()))
            .ReturnsAsync(run);

        var result = await _controller.CreateRun(new CreateCapitationRunRequest
        {
            RunType = CapitationRunType.Monthly,
            CapitationPeriod = new DateTime(2026, 3, 1),
            Criteria = new CapitationRunCriteria { LineOfBusiness = LineOfBusiness.Commercial },
            CreatedBy = "admin"
        });

        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        created!.StatusCode.Should().Be(201);
        (created.Value as CapitationRun)!.RunNumber.Should().Be("CAPRUN-2026-03-ABCD");
    }

    [Fact]
    public async Task CreateRun_InvalidCriteria_ReturnsBadRequest()
    {
        _runService.Setup(s => s.CreateRunAsync(It.IsAny<CreateCapitationRunRequest>(), It.IsAny<string>()))
            .ThrowsAsync(new ArgumentException("Monthly capitation runs require a LineOfBusiness"));

        var result = await _controller.CreateRun(new CreateCapitationRunRequest
        {
            RunType = CapitationRunType.Monthly,
            CapitationPeriod = new DateTime(2026, 3, 1),
            CreatedBy = "admin"
        });

        var bad = result.Result as BadRequestObjectResult;
        bad.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteRun_Success_ReturnsOk()
    {
        var run = new CapitationRun
        {
            Id = "run-1",
            Status = CapitationRunStatus.Completed,
            TotalStatements = 5,
            TotalMemberMonths = 250
        };
        _runService.Setup(s => s.ExecuteRunAsync("run-1")).ReturnsAsync(run);

        var result = await _controller.ExecuteRun("run-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CapitationRun)!.TotalStatements.Should().Be(5);
    }

    [Fact]
    public async Task ExecuteRun_InvalidState_ReturnsBadRequest()
    {
        _runService.Setup(s => s.ExecuteRunAsync("run-1"))
            .ThrowsAsync(new InvalidOperationException("Run is in Running state"));

        var result = await _controller.ExecuteRun("run-1");

        var bad = result.Result as BadRequestObjectResult;
        bad.Should().NotBeNull();
    }

    [Fact]
    public async Task GetRunById_NotFound_Returns404()
    {
        _runService.Setup(s => s.GetRunAsync("missing"))
            .ThrowsAsync(new InvalidOperationException("not found"));

        var result = await _controller.GetRunById("missing");

        var notFound = result.Result as NotFoundObjectResult;
        notFound.Should().NotBeNull();
    }

    [Fact]
    public async Task GetRuns_ReturnsOkWithList()
    {
        var runs = new List<CapitationRun>
        {
            new() { RunNumber = "CAPRUN-COM-2026-03-AAAA" },
            new() { RunNumber = "CAPRUN-MCD-2026-02-BBBB" }
        };
        _runService.Setup(s => s.GetRunsAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null))
            .ReturnsAsync(runs);

        var result = await _controller.GetRuns(null, null);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<CapitationRun>)!.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRuns_WithLobFilter_PassesLobToService()
    {
        var runs = new List<CapitationRun>
        {
            new() { RunNumber = "CAPRUN-MCD-2026-03-AAAA", LineOfBusiness = LineOfBusiness.Medicaid }
        };
        _runService.Setup(s => s.GetRunsAsync(null, null, LineOfBusiness.Medicaid))
            .ReturnsAsync(runs);

        var result = await _controller.GetRuns(null, null, LineOfBusiness.Medicaid);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<CapitationRun>)!.Should().HaveCount(1);
        _runService.Verify(s => s.GetRunsAsync(null, null, LineOfBusiness.Medicaid), Times.Once);
    }

    [Fact]
    public async Task CancelRun_Success_ReturnsNoContent()
    {
        _runService.Setup(s => s.CancelRunAsync("run-1")).Returns(Task.CompletedTask);

        var result = await _controller.CancelRun("run-1");

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task CancelRun_InvalidState_ReturnsBadRequest()
    {
        _runService.Setup(s => s.CancelRunAsync("run-1"))
            .ThrowsAsync(new InvalidOperationException("Can only cancel Pending"));

        var result = await _controller.CancelRun("run-1");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetRunStatements_ReturnsStatements()
    {
        var statements = new List<CapitationStatement>
        {
            new() { StatementNumber = "CAPSTMT-111-2026-03" },
            new() { StatementNumber = "CAPSTMT-222-2026-03" }
        };
        _statementRepo.Setup(r => r.GetByRunIdAsync("run-1")).ReturnsAsync(statements);

        var result = await _controller.GetRunStatements("run-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<CapitationStatement>)!.Should().HaveCount(2);
    }
}
