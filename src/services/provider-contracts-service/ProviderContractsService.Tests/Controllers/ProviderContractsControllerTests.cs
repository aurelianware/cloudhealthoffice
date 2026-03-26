using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ProviderContractsService.Controllers;
using ProviderContractsService.Models;
using ProviderContractsService.Repositories;

namespace ProviderContractsService.Tests.Controllers;

public class ProviderContractsControllerTests
{
    private readonly Mock<IProviderContractRepository> _contractRepo;
    private readonly ProviderContractsController _controller;

    public ProviderContractsControllerTests()
    {
        _contractRepo = new Mock<IProviderContractRepository>();
        var logger = new Mock<ILogger<ProviderContractsController>>();
        _controller = new ProviderContractsController(_contractRepo.Object, logger.Object);
    }

    private static ProviderContract CreateContract(
        string id = "c-1",
        string npi = "1234567890",
        ProviderContractStatus status = ProviderContractStatus.Draft,
        string? tin = "123456789") => new()
    {
        Id = id,
        TenantId = "tenant-1",
        ContractNumber = $"CTR-{npi}-2026",
        ProviderNPI = npi,
        ProviderName = "Dr. Chen",
        ProviderTin = tin,
        ProviderType = ProviderType.Individual,
        LineOfBusiness = LineOfBusiness.Commercial,
        PaymentMethodology = PaymentMethodology.FullCapitation,
        NetworkStatus = NetworkParticipationStatus.Participating,
        Status = status,
        EffectiveDate = new DateTime(2026, 1, 1),
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedBy = "admin",
        CapitationRateConfigIds = new List<string> { "cap-1", "cap-2" },
        FfsRateConfigIds = new List<string> { "ffs-1" }
    };

    #region SearchContracts

    [Fact]
    public async Task SearchContracts_WithFilters_ReturnsFilteredResults()
    {
        var contracts = new List<ProviderContract> { CreateContract(), CreateContract("c-2", "9876543210") };
        _contractRepo.Setup(r => r.SearchAsync("1234567890", LineOfBusiness.Commercial,
            ProviderContractStatus.Active, PaymentMethodology.FullCapitation,
            NetworkParticipationStatus.Participating, 1, 50))
            .ReturnsAsync(contracts);

        var result = await _controller.SearchContracts(
            npi: "1234567890", lob: LineOfBusiness.Commercial,
            status: ProviderContractStatus.Active,
            paymentMethodology: PaymentMethodology.FullCapitation,
            networkStatus: NetworkParticipationStatus.Participating);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var items = (ok!.Value as IEnumerable<ProviderContract>)!.ToList();
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchContracts_NoFilters_ReturnsAll()
    {
        var contracts = new List<ProviderContract> { CreateContract(), CreateContract("c-2") };
        _contractRepo.Setup(r => r.SearchAsync(null, null, null, null, null, 1, 50))
            .ReturnsAsync(contracts);

        var result = await _controller.SearchContracts();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<ProviderContract>)!.Should().HaveCount(2);
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
        (ok!.Value as ProviderContract)!.ContractNumber.Should().Be("CTR-1234567890-2026");
    }

    [Fact]
    public async Task GetContractById_NotFound_Returns404()
    {
        _contractRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ProviderContract?)null);

        var result = await _controller.GetContractById("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region GetContractByNumber

    [Fact]
    public async Task GetContractByNumber_Found_ReturnsOk()
    {
        _contractRepo.Setup(r => r.GetByContractNumberAsync("CTR-1234567890-2026"))
            .ReturnsAsync(CreateContract());

        var result = await _controller.GetContractByNumber("CTR-1234567890-2026");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as ProviderContract)!.Id.Should().Be("c-1");
    }

    [Fact]
    public async Task GetContractByNumber_NotFound_Returns404()
    {
        _contractRepo.Setup(r => r.GetByContractNumberAsync("CTR-NOPE-2026"))
            .ReturnsAsync((ProviderContract?)null);

        var result = await _controller.GetContractByNumber("CTR-NOPE-2026");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region CreateContract

    [Fact]
    public async Task CreateContract_AutoGeneratesContractNumber()
    {
        var contract = CreateContract();
        contract.ContractNumber = string.Empty; // force auto-generation
        _contractRepo.Setup(r => r.CreateAsync(It.IsAny<ProviderContract>()))
            .ReturnsAsync((ProviderContract c) => { c.Id = "new-id"; return c; });

        var result = await _controller.CreateContract(contract);

        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        var saved = created!.Value as ProviderContract;
        saved!.ContractNumber.Should().StartWith("CTR-1234567890-");
    }

    [Fact]
    public async Task CreateContract_ForcesDraftStatus()
    {
        var contract = CreateContract(status: ProviderContractStatus.Active);
        _contractRepo.Setup(r => r.CreateAsync(It.IsAny<ProviderContract>()))
            .ReturnsAsync((ProviderContract c) => c);

        var result = await _controller.CreateContract(contract);

        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        (created!.Value as ProviderContract)!.Status.Should().Be(ProviderContractStatus.Draft);
    }

    [Fact]
    public async Task CreateContract_ReturnsCreatedAtAction()
    {
        var contract = CreateContract();
        _contractRepo.Setup(r => r.CreateAsync(It.IsAny<ProviderContract>()))
            .ReturnsAsync((ProviderContract c) => { c.Id = "new-id"; return c; });

        var result = await _controller.CreateContract(contract);

        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        created!.StatusCode.Should().Be(201);
    }

    #endregion

    #region UpdateContract

    [Fact]
    public async Task UpdateContract_PreservesTenantIdCreatedAtCreatedBy()
    {
        var existing = CreateContract();
        existing.TenantId = "original-tenant";
        existing.CreatedAt = new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        existing.CreatedBy = "original-user";
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(existing);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<ProviderContract>()))
            .ReturnsAsync((ProviderContract c) => c);

        var updated = CreateContract();
        updated.TenantId = "hacker-tenant";
        updated.CreatedAt = DateTime.UtcNow;
        updated.CreatedBy = "hacker";
        var result = await _controller.UpdateContract("c-1", updated);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var saved = ok!.Value as ProviderContract;
        saved!.Id.Should().Be("c-1");
        saved.TenantId.Should().Be("original-tenant");
        saved.CreatedAt.Should().Be(new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc));
        saved.CreatedBy.Should().Be("original-user");
    }

    [Fact]
    public async Task UpdateContract_NotFound_Returns404()
    {
        _contractRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ProviderContract?)null);

        var result = await _controller.UpdateContract("missing", CreateContract());

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region ActivateContract — State Machine

    [Fact]
    public async Task ActivateContract_DraftToActive_Works()
    {
        var contract = CreateContract(status: ProviderContractStatus.Draft);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<ProviderContract>()))
            .ReturnsAsync((ProviderContract c) => c);

        var result = await _controller.ActivateContract("c-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as ProviderContract)!.Status.Should().Be(ProviderContractStatus.Active);
    }

    [Fact]
    public async Task ActivateContract_SuspendedToActive_Works()
    {
        var contract = CreateContract(status: ProviderContractStatus.Suspended);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<ProviderContract>()))
            .ReturnsAsync((ProviderContract c) => c);

        var result = await _controller.ActivateContract("c-1");

        var ok = result.Result as OkObjectResult;
        (ok!.Value as ProviderContract)!.Status.Should().Be(ProviderContractStatus.Active);
    }

    [Fact]
    public async Task ActivateContract_AlreadyActive_ReturnsBadRequest()
    {
        var contract = CreateContract(status: ProviderContractStatus.Active);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);

        var result = await _controller.ActivateContract("c-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ActivateContract_Terminated_ReturnsBadRequest()
    {
        var contract = CreateContract(status: ProviderContractStatus.Terminated);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);

        var result = await _controller.ActivateContract("c-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region SuspendContract — State Machine

    [Fact]
    public async Task SuspendContract_ActiveToSuspended_Works()
    {
        var contract = CreateContract(status: ProviderContractStatus.Active);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<ProviderContract>()))
            .ReturnsAsync((ProviderContract c) => c);

        var result = await _controller.SuspendContract("c-1",
            new SuspendContractRequest { Reason = "Under review" });

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as ProviderContract)!.Status.Should().Be(ProviderContractStatus.Suspended);
    }

    [Fact]
    public async Task SuspendContract_DraftToSuspended_ReturnsBadRequest()
    {
        var contract = CreateContract(status: ProviderContractStatus.Draft);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);

        var result = await _controller.SuspendContract("c-1",
            new SuspendContractRequest { Reason = "Nope" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region TerminateContract — State Machine

    [Fact]
    public async Task TerminateContract_ActiveContract_SetsTerminatedStatusAndFields()
    {
        var contract = CreateContract(status: ProviderContractStatus.Active);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<ProviderContract>()))
            .ReturnsAsync((ProviderContract c) => c);

        var termDate = new DateTime(2026, 12, 31);
        var result = await _controller.TerminateContract("c-1",
            new TerminateContractRequest { Reason = "Provider left network", TerminationDate = termDate });

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var terminated = ok!.Value as ProviderContract;
        terminated!.Status.Should().Be(ProviderContractStatus.Terminated);
        terminated.TerminationDate.Should().Be(termDate);
        terminated.TerminationReason.Should().Be("Provider left network");
    }

    [Fact]
    public async Task TerminateContract_AlreadyTerminated_ReturnsBadRequest()
    {
        var contract = CreateContract(status: ProviderContractStatus.Terminated);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);

        var result = await _controller.TerminateContract("c-1",
            new TerminateContractRequest { Reason = "Already done" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region ReinstateContract — State Machine

    [Fact]
    public async Task ReinstateContract_SuspendedToActive_Works()
    {
        var contract = CreateContract(status: ProviderContractStatus.Suspended);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<ProviderContract>()))
            .ReturnsAsync((ProviderContract c) => c);

        var result = await _controller.ReinstateContract("c-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as ProviderContract)!.Status.Should().Be(ProviderContractStatus.Active);
    }

    [Fact]
    public async Task ReinstateContract_ActiveToActive_ReturnsBadRequest()
    {
        var contract = CreateContract(status: ProviderContractStatus.Active);
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);

        var result = await _controller.ReinstateContract("c-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region TIN Masking — PII Security

    [Fact]
    public async Task SearchContracts_MasksTinToLastFourDigits()
    {
        var contracts = new List<ProviderContract> { CreateContract(tin: "123456789") };
        _contractRepo.Setup(r => r.SearchAsync(null, null, null, null, null, 1, 50))
            .ReturnsAsync(contracts);

        var result = await _controller.SearchContracts();

        var ok = result.Result as OkObjectResult;
        var items = (ok!.Value as IEnumerable<ProviderContract>)!.ToList();
        items.First().ProviderTin.Should().Be("***-**-6789");
    }

    [Fact]
    public async Task GetContractById_ReturnsFullTin()
    {
        var contract = CreateContract(tin: "123456789");
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);

        var result = await _controller.GetContractById("c-1");

        var ok = result.Result as OkObjectResult;
        (ok!.Value as ProviderContract)!.ProviderTin.Should().Be("123456789");
    }

    [Fact]
    public async Task SearchContracts_NullTin_HandledGracefully()
    {
        var contracts = new List<ProviderContract> { CreateContract(tin: null) };
        _contractRepo.Setup(r => r.SearchAsync(null, null, null, null, null, 1, 50))
            .ReturnsAsync(contracts);

        var result = await _controller.SearchContracts();

        var ok = result.Result as OkObjectResult;
        var items = (ok!.Value as IEnumerable<ProviderContract>)!.ToList();
        items.First().ProviderTin.Should().BeNull();
    }

    #endregion

    #region Amendments

    [Fact]
    public async Task AddAmendment_ExistingContract_AddsAndReturnsOk()
    {
        var contract = CreateContract();
        contract.Amendments = new List<ContractAmendment>();
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<ProviderContract>()))
            .ReturnsAsync((ProviderContract c) => c);

        var amendment = new ContractAmendment
        {
            EffectiveDate = new DateTime(2026, 7, 1),
            AmendmentType = "Rate Renegotiation",
            Description = "Adjusted capitation rates for Q3"
        };

        var result = await _controller.AddAmendment("c-1", amendment);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var saved = ok!.Value as ProviderContract;
        saved!.Amendments.Should().HaveCount(1);
        saved.Amendments.First().AmendmentType.Should().Be("Rate Renegotiation");
        saved.Amendments.First().Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AddAmendment_NonExistentContract_Returns404()
    {
        _contractRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ProviderContract?)null);

        var amendment = new ContractAmendment
        {
            EffectiveDate = DateTime.UtcNow,
            AmendmentType = "Test",
            Description = "Test"
        };

        var result = await _controller.AddAmendment("missing", amendment);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region Rate Config Children

    [Fact]
    public async Task GetRateConfigs_ReturnsCorrectIds()
    {
        var contract = CreateContract();
        _contractRepo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);

        var result = await _controller.GetRateConfigs("c-1");

        var ok = result as OkObjectResult;
        ok.Should().NotBeNull();

        // The anonymous object is returned — verify via dynamic/reflection
        var value = ok!.Value!;
        var contractIdProp = value.GetType().GetProperty("contractId");
        contractIdProp.Should().NotBeNull();
        contractIdProp!.GetValue(value).Should().Be("c-1");

        var capIdsProp = value.GetType().GetProperty("capitationRateConfigIds");
        capIdsProp.Should().NotBeNull();
        var capIds = capIdsProp!.GetValue(value) as List<string>;
        capIds.Should().BeEquivalentTo(new[] { "cap-1", "cap-2" });

        var ffsIdsProp = value.GetType().GetProperty("ffsRateConfigIds");
        ffsIdsProp.Should().NotBeNull();
        var ffsIds = ffsIdsProp!.GetValue(value) as List<string>;
        ffsIds.Should().BeEquivalentTo(new[] { "ffs-1" });
    }

    [Fact]
    public async Task GetRateConfigs_NotFound_Returns404()
    {
        _contractRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ProviderContract?)null);

        var result = await _controller.GetRateConfigs("missing");

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion
}
