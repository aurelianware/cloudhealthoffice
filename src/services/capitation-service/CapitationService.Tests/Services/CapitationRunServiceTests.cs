using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;

namespace CapitationService.Tests.Services;

public class CapitationRunServiceTests
{
    private readonly Mock<ICapitationRunRepository> _runRepo;
    private readonly Mock<ICapitationContractRepository> _contractRepo;
    private readonly Mock<ICapitationStatementRepository> _statementRepo;
    private readonly Mock<IHttpClientFactory> _httpClientFactory;
    private readonly CapitationRunService _service;

    // Test period: March 2026 (31 days)
    private static readonly DateTime TestPeriod = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TestPeriodEnd = new(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);

    public CapitationRunServiceTests()
    {
        _runRepo = new Mock<ICapitationRunRepository>();
        _contractRepo = new Mock<ICapitationContractRepository>();
        _statementRepo = new Mock<ICapitationStatementRepository>();
        _httpClientFactory = new Mock<IHttpClientFactory>();

        var logger = new Mock<ILogger<CapitationRunService>>();

        _service = new CapitationRunService(
            _runRepo.Object,
            _contractRepo.Object,
            _statementRepo.Object,
            _httpClientFactory.Object,
            logger.Object);
    }

    private static CapitationRun CreatePendingRun(string id = "run-1") => new()
    {
        Id = id,
        RunNumber = "CAPRUN-2026-03-ABCD",
        CapitationPeriod = TestPeriod,
        Status = CapitationRunStatus.Pending,
        Criteria = new CapitationRunCriteria()
    };

    private static CapitationContract CreateContract(
        string npi = "1234567890",
        string name = "Dr. Smith",
        decimal basePmpm = 50.00m,
        decimal withholdPct = 0.10m,
        bool riskAdjusted = false) => new()
    {
        Id = "contract-1",
        ContractNumber = $"CAP-{npi}-2026",
        ProviderNPI = npi,
        ProviderName = name,
        ContractType = ContractType.PrimaryCareOnly,
        LineOfBusiness = LineOfBusiness.Commercial,
        Status = CapitationRateConfigStatus.Active,
        EffectiveDate = new DateTime(2026, 1, 1),
        WithholdPercentage = withholdPct,
        RiskAdjusted = riskAdjusted,
        DefaultRiskScore = 1.0m,
        RateTiers = new List<CapitationRateTier>
        {
            new() { TierName = "Adult Male 18-34", AgeFrom = 18, AgeTo = 34, Gender = "M", AgeSexCategory = AgeSexCategory.AdultMale_18_34, BasePMPM = basePmpm },
            new() { TierName = "Adult Female 18-34", AgeFrom = 18, AgeTo = 34, Gender = "F", AgeSexCategory = AgeSexCategory.AdultFemale_18_34, BasePMPM = basePmpm },
            new() { TierName = "Child 2-11", AgeFrom = 2, AgeTo = 11, AgeSexCategory = AgeSexCategory.Child_2_11, BasePMPM = 30.00m },
            new() { TierName = "Senior 65+", AgeFrom = 65, AgeTo = 120, AgeSexCategory = AgeSexCategory.Senior_65Plus, BasePMPM = 120.00m }
        }
    };

    private static CapitationCoverageDto CreateCoverage(
        string memberId = "MEM001",
        string memberName = "John Doe",
        DateTime? effectiveDate = null,
        DateTime? terminationDate = null,
        DateTime? dob = null,
        string gender = "M",
        string pcpNpi = "1234567890") => new()
    {
        CoverageId = $"cov-{memberId}",
        MemberId = memberId,
        MemberName = memberName,
        PlanId = "PLAN-HMO-001",
        EffectiveDate = effectiveDate ?? new DateTime(2025, 1, 1),
        TerminationDate = terminationDate,
        DateOfBirth = dob ?? new DateTime(2000, 6, 15), // Age 25 in March 2026
        Gender = gender,
        PcpNpi = pcpNpi,
        PcpAssignmentDate = new DateTime(2025, 1, 1)
    };

    private void SetupCoverageServiceResponse(string npi, List<CapitationCoverageDto> coverages)
    {
        var handler = new MockHttpMessageHandler<List<CapitationCoverageDto>>(
            request => request.RequestUri!.PathAndQuery.Contains(npi) ? coverages : new());
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://coverage-service") };
        _httpClientFactory.Setup(f => f.CreateClient("CoverageService")).Returns(client);
    }

    private void SetupRiskScoreServiceResponse(decimal score = 1.0m)
    {
        var handler = new MockHttpMessageHandler<RiskScoreDto>(
            _ => new RiskScoreDto { RiskScore = score });
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://risk-adjustment-service") };
        _httpClientFactory.Setup(f => f.CreateClient("RiskAdjustmentService")).Returns(client);
    }

    private void SetupRiskScoreService404()
    {
        var handler = new MockHttpMessageHandler<RiskScoreDto>(_ => null);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://risk-adjustment-service") };
        _httpClientFactory.Setup(f => f.CreateClient("RiskAdjustmentService")).Returns(client);
    }

    private void SetupDefaultRepos()
    {
        _runRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationRun>()))
            .ReturnsAsync((CapitationRun r) => r);
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);
    }

    #region ExecuteRunAsync

    [Fact]
    public async Task ExecuteRunAsync_WithActiveContracts_GeneratesStatements()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract1 = CreateContract("1111111111", "Dr. Alpha");
        var contract2 = CreateContract("2222222222", "Dr. Beta");
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract1, contract2 });

        // Each PCP has 2 members
        var handler = new MockHttpMessageHandler<List<CapitationCoverageDto>>(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("1111111111"))
                return new List<CapitationCoverageDto>
                {
                    CreateCoverage("MEM001", pcpNpi: "1111111111"),
                    CreateCoverage("MEM002", pcpNpi: "1111111111", memberName: "Jane Doe", gender: "F")
                };
            if (path.Contains("2222222222"))
                return new List<CapitationCoverageDto>
                {
                    CreateCoverage("MEM003", pcpNpi: "2222222222", memberName: "Bob Smith")
                };
            return new List<CapitationCoverageDto>();
        });
        var coverageClient = new HttpClient(handler) { BaseAddress = new Uri("http://coverage-service") };
        _httpClientFactory.Setup(f => f.CreateClient("CoverageService")).Returns(coverageClient);
        SetupRiskScoreServiceResponse();

        var result = await _service.ExecuteRunAsync("run-1");

        result.Status.Should().Be(CapitationRunStatus.Completed);
        result.TotalStatements.Should().Be(2);
        result.TotalMemberMonths.Should().Be(3); // 2 + 1
        result.TotalProviders.Should().Be(2);
        result.StatementIds.Should().HaveCount(2);
        result.ExecutionCompletedAt.Should().NotBeNull();
        result.ExecutionDurationSeconds.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task ExecuteRunAsync_WithMidMonthEnrollment_ProratesCorrectly()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract = CreateContract(basePmpm: 100.00m, withholdPct: 0);
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract });

        // Member enrolled mid-month on March 16 (16 covered days out of 31)
        var midMonthCoverage = CreateCoverage(effectiveDate: new DateTime(2026, 3, 16));
        SetupCoverageServiceResponse("1234567890", new List<CapitationCoverageDto> { midMonthCoverage });
        SetupRiskScoreServiceResponse();

        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.ExecuteRunAsync("run-1");

        savedStatement.Should().NotBeNull();
        savedStatement!.LineItems.Should().ContainSingle();
        var item = savedStatement.LineItems[0];

        // 16 days covered (March 16-31) out of 31 days in March
        item.ProrationFactor.Should().BeApproximately(16m / 31m, 0.001m);
        item.GrossAmount.Should().BeApproximately(Math.Round(100.00m * (16m / 31m), 2), 0.01m);
        item.IsRetroactive.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteRunAsync_WithRiskAdjustment_AppliesRiskScores()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract = CreateContract(basePmpm: 50.00m, withholdPct: 0, riskAdjusted: true);
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract });

        SetupCoverageServiceResponse("1234567890", new List<CapitationCoverageDto>
        {
            CreateCoverage("MEM001")
        });
        SetupRiskScoreServiceResponse(1.5m);

        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.ExecuteRunAsync("run-1");

        savedStatement.Should().NotBeNull();
        var item = savedStatement!.LineItems[0];
        item.RiskScore.Should().Be(1.5m);
        item.BasePMPM.Should().Be(50.00m);
        item.AdjustedPMPM.Should().Be(75.00m); // 50 × 1.5
        item.GrossAmount.Should().Be(75.00m);
    }

    [Fact]
    public async Task ExecuteRunAsync_WithWithhold_CalculatesWithholdCorrectly()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract = CreateContract(basePmpm: 100.00m, withholdPct: 0.10m);
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract });

        SetupCoverageServiceResponse("1234567890", new List<CapitationCoverageDto>
        {
            CreateCoverage("MEM001")
        });
        SetupRiskScoreServiceResponse();

        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.ExecuteRunAsync("run-1");

        savedStatement.Should().NotBeNull();
        var item = savedStatement!.LineItems[0];
        item.GrossAmount.Should().Be(100.00m);
        item.WithholdAmount.Should().Be(10.00m); // 100 × 0.10
        item.NetAmount.Should().Be(90.00m);

        savedStatement.GrossCapitation.Should().Be(100.00m);
        savedStatement.WithholdAmount.Should().Be(10.00m);
        savedStatement.NetPayable.Should().Be(90.00m);
    }

    [Fact]
    public async Task ExecuteRunAsync_NoRiskScore_UsesDefaultScore()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract = CreateContract(basePmpm: 80.00m, withholdPct: 0, riskAdjusted: true);
        contract.DefaultRiskScore = 1.0m;
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract });

        SetupCoverageServiceResponse("1234567890", new List<CapitationCoverageDto>
        {
            CreateCoverage("MEM001")
        });
        SetupRiskScoreService404(); // 404 → defaults to 1.0

        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.ExecuteRunAsync("run-1");

        savedStatement.Should().NotBeNull();
        var item = savedStatement!.LineItems[0];
        item.RiskScore.Should().Be(1.0m);
        item.AdjustedPMPM.Should().Be(80.00m); // 80 × 1.0
    }

    [Fact]
    public async Task ExecuteRunAsync_ContractError_AddsWarning_ContinuesOthers()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        _runRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationRun>()))
            .ReturnsAsync((CapitationRun r) => r);

        var failContract = CreateContract("1111111111", "Dr. Fail");
        var okContract = CreateContract("2222222222", "Dr. OK");
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { failContract, okContract });

        // Both NPIs return members
        var handler = new MockHttpMessageHandler<List<CapitationCoverageDto>>(request =>
            new List<CapitationCoverageDto>
            {
                CreateCoverage("MEM001", pcpNpi: request.RequestUri!.PathAndQuery.Contains("1111111111")
                    ? "1111111111" : "2222222222")
            });
        var coverageClient = new HttpClient(handler) { BaseAddress = new Uri("http://coverage-service") };
        _httpClientFactory.Setup(f => f.CreateClient("CoverageService")).Returns(coverageClient);
        SetupRiskScoreServiceResponse();

        // Statement creation: fails for first contract (NPI 1111111111), succeeds for second
        var callCount = 0;
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) =>
            {
                callCount++;
                if (callCount == 1)
                    throw new Exception("Cosmos DB transient error");
                return s;
            });

        var result = await _service.ExecuteRunAsync("run-1");

        result.Status.Should().Be(CapitationRunStatus.Completed);
        result.TotalStatements.Should().Be(1);
        result.Warnings.Should().ContainSingle();
        result.Warnings[0].Should().Contain("1111111111");
    }

    [Fact]
    public async Task ExecuteRunAsync_WrongStatus_ThrowsInvalidOperation()
    {
        var run = CreatePendingRun();
        run.Status = CapitationRunStatus.Running;
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);

        var act = () => _service.ExecuteRunAsync("run-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Running*expected Pending*");
    }

    #endregion

    #region CancelRunAsync

    [Fact]
    public async Task CancelRunAsync_PendingRun_Cancels()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        _runRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationRun>()))
            .ReturnsAsync((CapitationRun r) => r);

        await _service.CancelRunAsync("run-1");

        _runRepo.Verify(r => r.UpdateAsync(It.Is<CapitationRun>(
            x => x.Status == CapitationRunStatus.Cancelled)), Times.Once);
    }

    [Fact]
    public async Task CancelRunAsync_CompletedRun_ThrowsInvalidOperation()
    {
        var run = CreatePendingRun();
        run.Status = CapitationRunStatus.Completed;
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);

        var act = () => _service.CancelRunAsync("run-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Pending*");
    }

    #endregion

    #region Statement lifecycle

    [Fact]
    public async Task ApproveStatementAsync_GeneratedStatement_Approves()
    {
        var statement = new CapitationStatement
        {
            Id = "stmt-1", StatementNumber = "CAPSTMT-123-2026-03",
            Status = CapitationStatementStatus.Generated
        };
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);

        var result = await _service.ApproveStatementAsync("stmt-1");

        result.Status.Should().Be(CapitationStatementStatus.Approved);
    }

    [Fact]
    public async Task VoidStatementAsync_PaidStatement_Throws()
    {
        var statement = new CapitationStatement
        {
            Id = "stmt-1", Status = CapitationStatementStatus.Paid
        };
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);

        var act = () => _service.VoidStatementAsync("stmt-1", "duplicate");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*paid*");
    }

    [Fact]
    public async Task VoidStatementAsync_GeneratedStatement_Voids()
    {
        var statement = new CapitationStatement
        {
            Id = "stmt-1", StatementNumber = "CAPSTMT-123",
            Status = CapitationStatementStatus.Generated
        };
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);

        var result = await _service.VoidStatementAsync("stmt-1", "error in data");

        result.Status.Should().Be(CapitationStatementStatus.Voided);
        result.Adjustments.Should().ContainSingle(a => a.Description.Contains("error in data"));
    }

    [Fact]
    public async Task HoldStatementAsync_GeneratedStatement_PutsOnHold()
    {
        var statement = new CapitationStatement
        {
            Id = "stmt-1", StatementNumber = "CAPSTMT-123",
            Status = CapitationStatementStatus.Generated
        };
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);

        var result = await _service.HoldStatementAsync("stmt-1", "under review");

        result.Status.Should().Be(CapitationStatementStatus.OnHold);
        result.Adjustments.Should().ContainSingle(a => a.Description.Contains("under review"));
    }

    [Fact]
    public async Task HoldStatementAsync_PaidStatement_Throws()
    {
        var statement = new CapitationStatement
        {
            Id = "stmt-1", Status = CapitationStatementStatus.Paid
        };
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);

        var act = () => _service.HoldStatementAsync("stmt-1", "reason");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ApproveStatementAsync_OnHoldStatement_Approves()
    {
        var statement = new CapitationStatement
        {
            Id = "stmt-1", Status = CapitationStatementStatus.OnHold
        };
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        _statementRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);

        var result = await _service.ApproveStatementAsync("stmt-1");

        result.Status.Should().Be(CapitationStatementStatus.Approved);
    }

    [Fact]
    public async Task ApproveStatementAsync_PaidStatement_Throws()
    {
        var statement = new CapitationStatement
        {
            Id = "stmt-1", Status = CapitationStatementStatus.Paid
        };
        _statementRepo.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);

        var act = () => _service.ApproveStatementAsync("stmt-1");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateRunAsync_NormalizesPeriodToFirstOfMonth()
    {
        CapitationRun? savedRun = null;
        _runRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationRun>()))
            .Callback<CapitationRun>(r => savedRun = r)
            .ReturnsAsync((CapitationRun r) => r);

        await _service.CreateRunAsync(new CreateCapitationRunRequest
        {
            CapitationPeriod = new DateTime(2026, 3, 15), // Mid-month
            CreatedBy = "admin",
            Criteria = new CapitationRunCriteria { LineOfBusiness = LineOfBusiness.Commercial }
        }, "admin");

        savedRun.Should().NotBeNull();
        savedRun!.CapitationPeriod.Day.Should().Be(1); // Normalized to first
        savedRun.RunNumber.Should().StartWith("CAPRUN-COM-2026-03-");
        savedRun.Status.Should().Be(CapitationRunStatus.Pending);
    }

    [Fact]
    public async Task GetRunAsync_NotFound_Throws()
    {
        _runRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((CapitationRun?)null);

        var act = () => _service.GetRunAsync("missing");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task GetRunsAsync_DelegatesToRepo()
    {
        var runs = new List<CapitationRun> { CreatePendingRun() };
        _runRepo.Setup(r => r.SearchAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), (CapitationRunStatus?)null, (LineOfBusiness?)null))
            .ReturnsAsync(runs);

        var result = await _service.GetRunsAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

        result.Should().HaveCount(1);
    }

    #endregion

    #region GetCapitationSummaryAsync

    [Fact]
    public async Task GetCapitationSummaryAsync_ReturnsCorrectTotals()
    {
        var stmts = new List<CapitationStatement>
        {
            new() { ProviderNPI = "111", ContractId = "c1", MemberMonths = 5, GrossCapitation = 500, WithholdAmount = 50, NetPayable = 450 },
            new() { ProviderNPI = "222", ContractId = "c2", MemberMonths = 3, GrossCapitation = 300, WithholdAmount = 30, NetPayable = 270 }
        };
        _statementRepo.Setup(r => r.GetByProviderNpiAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(stmts);

        var contracts = new List<CapitationContract>
        {
            new() { Id = "c1", LineOfBusiness = LineOfBusiness.Commercial, ContractType = ContractType.PrimaryCareOnly },
            new() { Id = "c2", LineOfBusiness = LineOfBusiness.Medicaid, ContractType = ContractType.GlobalCapitation }
        };
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>())).ReturnsAsync(contracts);

        var summary = await _service.GetCapitationSummaryAsync(new DateTime(2026, 3, 15));

        summary.Period.Should().Be(new DateTime(2026, 3, 1));
        summary.TotalProviders.Should().Be(2);
        summary.TotalMemberMonths.Should().Be(8);
        summary.TotalGrossCapitation.Should().Be(800);
        summary.TotalNetPayable.Should().Be(720);
        summary.ByLineOfBusiness.Should().ContainKey("Commercial");
        summary.ByLineOfBusiness.Should().ContainKey("Medicaid");
        summary.ByContractType.Should().ContainKey("PrimaryCareOnly");
        summary.ByContractType.Should().ContainKey("GlobalCapitation");
    }

    #endregion

    #region ExecuteRunAsync — criteria filters and edge cases

    [Fact]
    public async Task ExecuteRunAsync_WithNpiFilter_OnlyIncludesMatchingContracts()
    {
        var run = CreatePendingRun();
        run.Criteria.ProviderNPIs = new List<string> { "2222222222" };
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract1 = CreateContract("1111111111", "Dr. Alpha");
        var contract2 = CreateContract("2222222222", "Dr. Beta");
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract1, contract2 });

        SetupCoverageServiceResponse("2222222222", new List<CapitationCoverageDto>
        {
            CreateCoverage("MEM001", pcpNpi: "2222222222")
        });
        SetupRiskScoreServiceResponse();

        var result = await _service.ExecuteRunAsync("run-1");

        result.TotalStatements.Should().Be(1);
        result.TotalProviders.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteRunAsync_WithPlanFilter_OnlyIncludesMatchingContracts()
    {
        var run = CreatePendingRun();
        run.Criteria.PlanIds = new List<string> { "PLAN-SPECIAL" };
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract1 = CreateContract("1111111111");
        contract1.PlanIds = new List<string> { "PLAN-HMO" };
        var contract2 = CreateContract("2222222222");
        contract2.PlanIds = new List<string> { "PLAN-SPECIAL" };
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract1, contract2 });

        SetupCoverageServiceResponse("2222222222", new List<CapitationCoverageDto>
        {
            CreateCoverage("MEM001", pcpNpi: "2222222222")
        });
        SetupRiskScoreServiceResponse();

        var result = await _service.ExecuteRunAsync("run-1");

        result.TotalStatements.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteRunAsync_FatalError_SetsFailedStatus()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        _runRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationRun>()))
            .ReturnsAsync((CapitationRun r) => r);

        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ThrowsAsync(new Exception("Database connection lost"));

        var result = await _service.ExecuteRunAsync("run-1");

        result.Status.Should().Be(CapitationRunStatus.Failed);
        result.Errors.Should().ContainSingle().Which.Should().Contain("Database connection lost");
    }

    [Fact]
    public async Task ExecuteRunAsync_WithRetroAdjustment_AddsPcpChangeAdjustment()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract = CreateContract("1234567890", "Dr. Chen", basePmpm: 50, withholdPct: 0);
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract });

        // Member reassigned FROM this provider TO another
        var coverage = CreateCoverage("MEM-RETRO", pcpNpi: "9999999999");
        coverage.PreviousPcpNpi = "1234567890"; // Was with Dr. Chen
        coverage.PcpNpi = "9999999999";         // Now with someone else

        SetupCoverageServiceResponse("1234567890", new List<CapitationCoverageDto> { coverage });
        SetupRiskScoreServiceResponse();

        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.ExecuteRunAsync("run-1");

        savedStatement.Should().NotBeNull();
        savedStatement!.Adjustments.Should().Contain(a =>
            a.Type == CapitationAdjustmentType.RetroDisenrollment &&
            a.RelatedMemberId == "MEM-RETRO");
    }

    [Fact]
    public async Task ExecuteRunAsync_CoverageOutsidePeriod_Excluded()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract = CreateContract(basePmpm: 50, withholdPct: 0);
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract });

        // One active, one terminated before period, one future
        var coverages = new List<CapitationCoverageDto>
        {
            CreateCoverage("MEM-ACTIVE"),
            CreateCoverage("MEM-TERM", terminationDate: new DateTime(2026, 2, 15)),  // Before March
            CreateCoverage("MEM-FUTURE", effectiveDate: new DateTime(2026, 4, 1))    // After March
        };
        SetupCoverageServiceResponse("1234567890", coverages);
        SetupRiskScoreServiceResponse();

        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.ExecuteRunAsync("run-1");

        savedStatement.Should().NotBeNull();
        savedStatement!.LineItems.Should().ContainSingle(); // Only MEM-ACTIVE
        savedStatement.LineItems[0].MemberId.Should().Be("MEM-ACTIVE");
    }

    [Fact]
    public async Task ExecuteRunAsync_ContractWithPlanFilter_FiltersCoverages()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract = CreateContract(basePmpm: 50, withholdPct: 0);
        contract.PlanIds = new List<string> { "PLAN-HMO-001" };
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract });

        var coverages = new List<CapitationCoverageDto>
        {
            CreateCoverage("MEM-HMO"),   // PlanId defaults to PLAN-HMO-001
            CreateCoverage("MEM-PPO")
        };
        coverages[1].PlanId = "PLAN-PPO-001"; // Different plan
        SetupCoverageServiceResponse("1234567890", coverages);
        SetupRiskScoreServiceResponse();

        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.ExecuteRunAsync("run-1");

        savedStatement!.LineItems.Should().ContainSingle();
        savedStatement.LineItems[0].MemberId.Should().Be("MEM-HMO");
    }

    [Fact]
    public async Task ExecuteRunAsync_InfantMember_ResolvesCorrectTier()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract = CreateContract(withholdPct: 0);
        // Add infant tier
        contract.RateTiers.Insert(0, new CapitationRateTier
        {
            TierName = "Infant", AgeFrom = 0, AgeTo = 1,
            AgeSexCategory = AgeSexCategory.Infant_0_1, BasePMPM = 60.00m
        });
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract });

        var infant = CreateCoverage("MEM-BABY", dob: new DateTime(2025, 10, 1), gender: "M"); // 5 months old
        SetupCoverageServiceResponse("1234567890", new List<CapitationCoverageDto> { infant });
        SetupRiskScoreServiceResponse();

        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.ExecuteRunAsync("run-1");

        savedStatement!.LineItems[0].BasePMPM.Should().Be(60.00m);
        savedStatement.LineItems[0].AgeSexCategory.Should().Be(AgeSexCategory.Infant_0_1);
    }

    [Fact]
    public async Task ExecuteRunAsync_SeniorMember_ResolvesCorrectTier()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract = CreateContract(withholdPct: 0);
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract });

        var senior = CreateCoverage("MEM-SENIOR", dob: new DateTime(1950, 6, 1), gender: "F"); // 75 years old
        SetupCoverageServiceResponse("1234567890", new List<CapitationCoverageDto> { senior });
        SetupRiskScoreServiceResponse();

        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.ExecuteRunAsync("run-1");

        var item = savedStatement!.LineItems[0];
        item.AgeSexCategory.Should().Be(AgeSexCategory.Senior_65Plus);
    }

    [Fact]
    public async Task ExecuteRunAsync_UnknownGenderAdult_DefaultsToMaleTier()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract = CreateContract(basePmpm: 50, withholdPct: 0);
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract });

        var member = CreateCoverage("MEM-UNK", gender: "U"); // Unknown gender, age 28
        SetupCoverageServiceResponse("1234567890", new List<CapitationCoverageDto> { member });
        SetupRiskScoreServiceResponse();

        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.ExecuteRunAsync("run-1");

        savedStatement!.LineItems[0].AgeSexCategory.Should().Be(AgeSexCategory.AdultMale_18_34);
    }

    [Fact]
    public async Task ExecuteRunAsync_MidAgeRanges_ResolvesCorrectly()
    {
        var run = CreatePendingRun();
        _runRepo.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        SetupDefaultRepos();

        var contract = CreateContract(withholdPct: 0);
        _contractRepo.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { contract });

        // Ages covering multiple brackets: 15 (adolescent), 40 (adult 35-44), 50 (45-54), 60 (55-64)
        var coverages = new List<CapitationCoverageDto>
        {
            CreateCoverage("MEM-TEEN", dob: new DateTime(2011, 1, 1), gender: "F"),  // 15
            CreateCoverage("MEM-40M", dob: new DateTime(1986, 1, 1), gender: "M"),   // 40
            CreateCoverage("MEM-50F", dob: new DateTime(1976, 1, 1), gender: "F"),   // 50
            CreateCoverage("MEM-60M", dob: new DateTime(1966, 1, 1), gender: "M"),   // 60
        };
        SetupCoverageServiceResponse("1234567890", coverages);
        SetupRiskScoreServiceResponse();

        CapitationStatement? savedStatement = null;
        _statementRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(s => savedStatement = s)
            .ReturnsAsync((CapitationStatement s) => s);

        await _service.ExecuteRunAsync("run-1");

        savedStatement!.LineItems.Should().HaveCount(4);
        savedStatement.LineItems.Should().Contain(i => i.AgeSexCategory == AgeSexCategory.Adolescent_12_17);
        savedStatement.LineItems.Should().Contain(i => i.AgeSexCategory == AgeSexCategory.AdultMale_35_44);
        savedStatement.LineItems.Should().Contain(i => i.AgeSexCategory == AgeSexCategory.AdultFemale_45_54);
        savedStatement.LineItems.Should().Contain(i => i.AgeSexCategory == AgeSexCategory.AdultMale_55_64);
    }

    #endregion
}

/// <summary>
/// Generic mock HTTP handler for simulating service-to-service responses
/// </summary>
internal class MockHttpMessageHandler<T> : HttpMessageHandler where T : class
{
    private readonly Func<HttpRequestMessage, T?> _responseFactory;

    public MockHttpMessageHandler(Func<HttpRequestMessage, T?> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        T? responseObj;
        try
        {
            responseObj = _responseFactory(request);
        }
        catch (Exception)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }

        if (responseObj == null)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var json = JsonSerializer.Serialize(responseObj);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });
    }
}
