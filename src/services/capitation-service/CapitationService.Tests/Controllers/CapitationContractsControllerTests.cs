using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using CapitationService.Controllers;
using CapitationService.Models;
using CapitationService.Repositories;

namespace CapitationService.Tests.Controllers;

public class CapitationContractsControllerTests
{
    private readonly Mock<ICapitationContractRepository> _contractRepo;
    private readonly CapitationContractsController _controller;

    public CapitationContractsControllerTests()
    {
        _contractRepo = new Mock<ICapitationContractRepository>();
        var logger = new Mock<ILogger<CapitationContractsController>>();
        _controller = new CapitationContractsController(_contractRepo.Object, logger.Object);
    }

    private static CapitationContract CreateContract(
        string id = "c-1",
        string npi = "1234567890",
        CapitationRateConfigStatus status = CapitationRateConfigStatus.Draft) => new()
    {
        Id = id,
        ContractId = "pc-1",
        ContractNumber = $"CAP-{npi}-2026",
        ProviderNPI = npi,
        ProviderName = "Dr. Chen",
        ContractType = ContractType.PrimaryCareOnly,
        LineOfBusiness = LineOfBusiness.Commercial,
        Status = status,
        EffectiveDate = new DateTime(2026, 1, 1),
        WithholdPercentage = 0.10m,
        CreatedAt = DateTime.UtcNow,
        CreatedBy = "admin"
    };

    #region SearchContracts

    [Fact]
    public async Task SearchContracts_NoFilters_ReturnsAll()
    {
        var contracts = new List<CapitationContract> { CreateContract(), CreateContract("c-2", "9876543210") };
        _contractRepo.Setup(r => r.SearchAsync(null, null, null, null, 1, 50)).ReturnsAsync(contracts);

        var result = await _controller.SearchContracts();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<CapitationContract>)!.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchContracts_ByPlanId_UsesGetByPlanId()
    {
        var contracts = new List<CapitationContract> { CreateContract() };
        _contractRepo.Setup(r => r.GetByPlanIdAsync("PLAN-HMO")).ReturnsAsync(contracts);

        var result = await _controller.SearchContracts(planId: "PLAN-HMO");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        _contractRepo.Verify(r => r.GetByPlanIdAsync("PLAN-HMO"), Times.Once);
        _contractRepo.Verify(r => r.SearchAsync(It.IsAny<string>(), It.IsAny<LineOfBusiness?>(),
            It.IsAny<ContractType?>(), It.IsAny<CapitationRateConfigStatus?>(),
            It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SearchContracts_WithFilters_PassesThrough()
    {
        _contractRepo.Setup(r => r.SearchAsync("1234567890", LineOfBusiness.Commercial,
            ContractType.PrimaryCareOnly, CapitationRateConfigStatus.Active, 1, 50))
            .ReturnsAsync(new List<CapitationContract>());

        await _controller.SearchContracts(
            npi: "1234567890", lob: LineOfBusiness.Commercial,
            type: ContractType.PrimaryCareOnly, status: CapitationRateConfigStatus.Active);

        _contractRepo.Verify(r => r.SearchAsync("1234567890", LineOfBusiness.Commercial,
            ContractType.PrimaryCareOnly, CapitationRateConfigStatus.Active, 1, 50), Times.Once);
    }

    #endregion

    #region GetContractById

    [Fact]
    public async Task GetContractById_Found_ReturnsOk()
    {
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(CreateContract());

        var result = await _controller.GetContractById("c-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CapitationContract)!.ContractNumber.Should().Be("CAP-1234567890-2026");
    }

    [Fact]
    public async Task GetContractById_NotFound_Returns404()
    {
        _contractRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((CapitationContract?)null);

        var result = await _controller.GetContractById("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region CreateContract

    [Fact]
    public async Task CreateContract_ReturnsCreatedAtAction()
    {
        var contract = CreateContract();
        _contractRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationContract>()))
            .ReturnsAsync((CapitationContract c) => { c.Id = "new-id"; return c; });

        var result = await _controller.CreateContract(contract);

        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        created!.StatusCode.Should().Be(201);
        (created.Value as CapitationContract)!.Status.Should().Be(CapitationRateConfigStatus.Draft);
    }

    [Fact]
    public async Task CreateContract_ForcesDraftStatus()
    {
        var contract = CreateContract(status: CapitationRateConfigStatus.Active);
        _contractRepo.Setup(r => r.CreateAsync(It.IsAny<CapitationContract>()))
            .ReturnsAsync((CapitationContract c) => c);

        var result = await _controller.CreateContract(contract);

        var created = result.Result as CreatedAtActionResult;
        (created!.Value as CapitationContract)!.Status.Should().Be(CapitationRateConfigStatus.Draft);
    }

    #endregion

    #region UpdateContract

    [Fact]
    public async Task UpdateContract_Found_ReturnsOk()
    {
        var existing = CreateContract();
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(existing);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationContract>()))
            .ReturnsAsync((CapitationContract c) => c);

        var updated = CreateContract();
        updated.ProviderName = "Dr. Chen Updated";
        var result = await _controller.UpdateContract("c-1", updated);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var saved = ok!.Value as CapitationContract;
        saved!.Id.Should().Be("c-1");
        saved.CreatedAt.Should().Be(existing.CreatedAt); // Preserved
        saved.CreatedBy.Should().Be(existing.CreatedBy); // Preserved
    }

    [Fact]
    public async Task UpdateContract_NotFound_Returns404()
    {
        _contractRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((CapitationContract?)null);

        var result = await _controller.UpdateContract("missing", CreateContract());

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region ActivateContract

    [Fact]
    public async Task ActivateContract_DraftContract_Activates()
    {
        var contract = CreateContract(status: CapitationRateConfigStatus.Draft);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationContract>()))
            .ReturnsAsync((CapitationContract c) => c);

        var result = await _controller.ActivateContract("c-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CapitationContract)!.Status.Should().Be(CapitationRateConfigStatus.Active);
    }

    [Fact]
    public async Task ActivateContract_SuspendedContract_Activates()
    {
        var contract = CreateContract(status: CapitationRateConfigStatus.Suspended);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationContract>()))
            .ReturnsAsync((CapitationContract c) => c);

        var result = await _controller.ActivateContract("c-1");

        var ok = result.Result as OkObjectResult;
        (ok!.Value as CapitationContract)!.Status.Should().Be(CapitationRateConfigStatus.Active);
    }

    [Fact]
    public async Task ActivateContract_AlreadyActive_ReturnsBadRequest()
    {
        var contract = CreateContract(status: CapitationRateConfigStatus.Active);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);

        var result = await _controller.ActivateContract("c-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ActivateContract_Terminated_ReturnsBadRequest()
    {
        var contract = CreateContract(status: CapitationRateConfigStatus.Terminated);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);

        var result = await _controller.ActivateContract("c-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ActivateContract_NotFound_Returns404()
    {
        _contractRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((CapitationContract?)null);

        var result = await _controller.ActivateContract("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region TerminateContract

    [Fact]
    public async Task TerminateContract_ActiveContract_Terminates()
    {
        var contract = CreateContract(status: CapitationRateConfigStatus.Active);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationContract>()))
            .ReturnsAsync((CapitationContract c) => c);

        var result = await _controller.TerminateContract("c-1",
            new TerminateContractRequest { Reason = "Provider left network" });

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var terminated = ok!.Value as CapitationContract;
        terminated!.Status.Should().Be(CapitationRateConfigStatus.Terminated);
        terminated.TerminationDate.Should().NotBeNull();
    }

    [Fact]
    public async Task TerminateContract_WithCustomDate_UsesProvidedDate()
    {
        var contract = CreateContract(status: CapitationRateConfigStatus.Active);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<CapitationContract>()))
            .ReturnsAsync((CapitationContract c) => c);

        var termDate = new DateTime(2026, 12, 31);
        var result = await _controller.TerminateContract("c-1",
            new TerminateContractRequest { Reason = "End of year", TerminationDate = termDate });

        var ok = result.Result as OkObjectResult;
        (ok!.Value as CapitationContract)!.TerminationDate.Should().Be(termDate);
    }

    [Fact]
    public async Task TerminateContract_AlreadyTerminated_ReturnsBadRequest()
    {
        var contract = CreateContract(status: CapitationRateConfigStatus.Terminated);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);

        var result = await _controller.TerminateContract("c-1",
            new TerminateContractRequest { Reason = "Already done" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task TerminateContract_NotFound_Returns404()
    {
        _contractRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((CapitationContract?)null);

        var result = await _controller.TerminateContract("missing",
            new TerminateContractRequest { Reason = "test" });

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion
}
