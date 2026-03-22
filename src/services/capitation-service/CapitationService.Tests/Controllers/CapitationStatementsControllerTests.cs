using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using CapitationService.Controllers;
using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;

namespace CapitationService.Tests.Controllers;

public class CapitationStatementsControllerTests
{
    private readonly Mock<ICapitationRunService> _runService;
    private readonly Mock<ICapitationStatementRepository> _statementRepo;
    private readonly Mock<ICapitationContractRepository> _contractRepo;
    private readonly Mock<ICapitationEraService> _eraService;
    private readonly CapitationStatementsController _controller;

    public CapitationStatementsControllerTests()
    {
        _runService = new Mock<ICapitationRunService>();
        _statementRepo = new Mock<ICapitationStatementRepository>();
        _contractRepo = new Mock<ICapitationContractRepository>();
        _eraService = new Mock<ICapitationEraService>();
        var logger = new Mock<ILogger<CapitationStatementsController>>();

        _controller = new CapitationStatementsController(
            _runService.Object,
            _statementRepo.Object,
            _contractRepo.Object,
            _eraService.Object,
            logger.Object);
    }

    private static CapitationStatement CreateStatement(string id = "stmt-1") => new()
    {
        Id = id,
        StatementNumber = "CAPSTMT-1234567890-2026-03",
        ContractId = "contract-1",
        ProviderNPI = "1234567890",
        ProviderName = "Dr. Chen",
        Status = CapitationStatementStatus.Generated,
        MemberMonths = 5,
        GrossCapitation = 500,
        WithholdAmount = 50,
        NetPayable = 450
    };

    #region Search / Get

    [Fact]
    public async Task SearchStatements_ByNpi_ReturnsOk()
    {
        var statements = new List<CapitationStatement> { CreateStatement() };
        _statementRepo.Setup(r => r.GetByProviderNpiAsync("1234567890", null, null))
            .ReturnsAsync(statements);

        var result = await _controller.SearchStatements(npi: "1234567890");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<CapitationStatement>)!.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetStatementById_Found_ReturnsOk()
    {
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(CreateStatement());

        var result = await _controller.GetStatementById("stmt-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CapitationStatement)!.StatementNumber.Should().Be("CAPSTMT-1234567890-2026-03");
    }

    [Fact]
    public async Task GetStatementById_NotFound_Returns404()
    {
        _statementRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((CapitationStatement?)null);

        var result = await _controller.GetStatementById("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetUnpaidStatements_ReturnsOk()
    {
        _statementRepo.Setup(r => r.GetUnpaidStatementsAsync())
            .ReturnsAsync(new List<CapitationStatement> { CreateStatement(), CreateStatement("stmt-2") });

        var result = await _controller.GetUnpaidStatements();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<CapitationStatement>)!.Should().HaveCount(2);
    }

    #endregion

    #region Approve / Void / Hold

    [Fact]
    public async Task ApproveStatement_Success_ReturnsOk()
    {
        var stmt = CreateStatement();
        stmt.Status = CapitationStatementStatus.Approved;
        _runService.Setup(s => s.ApproveStatementAsync("stmt-1")).ReturnsAsync(stmt);

        var result = await _controller.ApproveStatement("stmt-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CapitationStatement)!.Status.Should().Be(CapitationStatementStatus.Approved);
    }

    [Fact]
    public async Task ApproveStatement_InvalidState_ReturnsBadRequest()
    {
        _runService.Setup(s => s.ApproveStatementAsync("stmt-1"))
            .ThrowsAsync(new InvalidOperationException("already paid"));

        var result = await _controller.ApproveStatement("stmt-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task VoidStatement_Success_ReturnsOk()
    {
        var stmt = CreateStatement();
        stmt.Status = CapitationStatementStatus.Voided;
        _runService.Setup(s => s.VoidStatementAsync("stmt-1", "duplicate")).ReturnsAsync(stmt);

        var result = await _controller.VoidStatement("stmt-1", new ReasonRequest { Reason = "duplicate" });

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CapitationStatement)!.Status.Should().Be(CapitationStatementStatus.Voided);
    }

    [Fact]
    public async Task HoldStatement_Success_ReturnsOk()
    {
        var stmt = CreateStatement();
        stmt.Status = CapitationStatementStatus.OnHold;
        _runService.Setup(s => s.HoldStatementAsync("stmt-1", "under review")).ReturnsAsync(stmt);

        var result = await _controller.HoldStatement("stmt-1", new ReasonRequest { Reason = "under review" });

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CapitationStatement)!.Status.Should().Be(CapitationStatementStatus.OnHold);
    }

    #endregion

    #region Summary

    [Fact]
    public async Task GetCapitationSummary_ReturnsOk()
    {
        var summary = new CapitationPeriodSummary
        {
            Period = new DateTime(2026, 3, 1),
            TotalProviders = 3,
            TotalMemberMonths = 20,
            TotalNetPayable = 5000m
        };
        _runService.Setup(s => s.GetCapitationSummaryAsync(It.IsAny<DateTime>())).ReturnsAsync(summary);

        var result = await _controller.GetCapitationSummary(new DateTime(2026, 3, 1));

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CapitationPeriodSummary)!.TotalProviders.Should().Be(3);
    }

    #endregion

    #region ERA Generation

    [Fact]
    public async Task GenerateEra_Success_ReturnsTextPlain()
    {
        var stmt = CreateStatement();
        stmt.ContractId = "contract-1";
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(stmt);
        _contractRepo.Setup(r => r.GetByIdAsync("contract-1")).ReturnsAsync(new CapitationContract
        {
            Id = "contract-1", ContractNumber = "CAP-123-2026"
        });
        _eraService.Setup(s => s.Generate835ForStatement(
                It.IsAny<CapitationStatement>(),
                It.IsAny<CapitationContract>(),
                It.IsAny<CapitationEraTradingPartnerInfo>()))
            .Returns("ISA*00*...");

        var result = await _controller.GenerateEra("stmt-1", null);

        var content = result as ContentResult;
        content.Should().NotBeNull();
        content!.ContentType.Should().Be("text/plain");
        content.Content.Should().StartWith("ISA*");
    }

    [Fact]
    public async Task GenerateEra_StatementNotFound_Returns404()
    {
        _statementRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((CapitationStatement?)null);

        var result = await _controller.GenerateEra("missing", null);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GenerateEra_ContractNotFound_Returns400()
    {
        var stmt = CreateStatement();
        stmt.ContractId = "missing-contract";
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(stmt);
        _contractRepo.Setup(r => r.GetByIdAsync("missing-contract")).ReturnsAsync((CapitationContract?)null);

        var result = await _controller.GenerateEra("stmt-1", null);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region SearchStatements additional paths

    [Fact]
    public async Task SearchStatements_ByStatus_ReturnsOk()
    {
        var statements = new List<CapitationStatement> { CreateStatement() };
        _statementRepo.Setup(r => r.GetByStatusAsync(CapitationStatementStatus.Approved))
            .ReturnsAsync(statements);

        var result = await _controller.SearchStatements(status: CapitationStatementStatus.Approved);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
    }

    [Fact]
    public async Task SearchStatements_PeriodOnlyWithoutNpiOrStatus_Returns400()
    {
        var result = await _controller.SearchStatements(
            periodFrom: new DateTime(2026, 3, 1),
            periodTo: new DateTime(2026, 3, 31));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SearchStatements_NoFilters_ReturnsGeneratedStatements()
    {
        var statements = new List<CapitationStatement> { CreateStatement() };
        _statementRepo.Setup(r => r.GetByStatusAsync(CapitationStatementStatus.Generated))
            .ReturnsAsync(statements);

        var result = await _controller.SearchStatements();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
    }

    [Fact]
    public async Task HoldStatement_InvalidState_ReturnsBadRequest()
    {
        _runService.Setup(s => s.HoldStatementAsync("stmt-1", "reason"))
            .ThrowsAsync(new InvalidOperationException("Cannot hold Paid"));

        var result = await _controller.HoldStatement("stmt-1", new ReasonRequest { Reason = "reason" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task VoidStatement_InvalidState_ReturnsBadRequest()
    {
        _runService.Setup(s => s.VoidStatementAsync("stmt-1", "reason"))
            .ThrowsAsync(new InvalidOperationException("Cannot void Paid"));

        var result = await _controller.VoidStatement("stmt-1", new ReasonRequest { Reason = "reason" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion
}
