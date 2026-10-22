using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ProviderContractsService.Controllers;
using ProviderContractsService.Models;
using ProviderContractsService.Repositories;
using EngineDomain = CloudHealthOffice.FeeScheduleEngine.Domain;
using EngineModels = CloudHealthOffice.FeeScheduleEngine.Models;

namespace ProviderContractsService.Tests.Controllers;

/// <summary>
/// One contract writer (ADR 016 phase 2): the rate sheet lives on the legal
/// contract, a fee-for-service rate config is one term on it rather than a
/// document in a service with no API, and the contract projects to the
/// engine contract a claim would actually be priced from.
/// </summary>
public class ReimbursementTermsTests
{
    private readonly Mock<IProviderContractRepository> _contractRepo = new();
    private readonly ProviderContractsController _controller;

    public ReimbursementTermsTests()
        => _controller = new ProviderContractsController(
            _contractRepo.Object, new Mock<ILogger<ProviderContractsController>>().Object);

    // ── The rate sheet ────────────────────────────────────────────────

    [Fact]
    public async Task ReplaceTerms_StoresTheSheetInOrder()
    {
        var contract = Contract();
        Returns(contract);

        var result = await _controller.ReplaceReimbursementTerms("c-1", new ReimbursementTermsRequest
        {
            Terms = [CarveOutTerm(), DefaultTerm()],
        });

        Ok(result).Should().HaveCount(2);
        contract.ReimbursementTerms[0].FeeScheduleId.Should().Be("ortho-2026");
        contract.ReimbursementTerms[1].FeeScheduleId.Should().Be("comm-2026");
        _contractRepo.Verify(r => r.UpdateAsync(contract), Times.Once);
    }

    [Fact]
    public async Task ReplaceTerms_GivesAnIdToATermThatHasNone()
    {
        var contract = Contract();
        Returns(contract);

        var term = DefaultTerm();
        term.Id = string.Empty;

        await _controller.ReplaceReimbursementTerms("c-1", new ReimbursementTermsRequest { Terms = [term] });

        contract.ReimbursementTerms.Single().Id.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ReplaceTerms_RejectsATermThatCannotPrice()
    {
        Returns(Contract());

        var term = DefaultTerm();
        term.FeeScheduleId = null;

        var result = await _controller.ReplaceReimbursementTerms(
            "c-1", new ReimbursementTermsRequest { Terms = [term] });

        Details(result).Should().ContainMatch("term 1: feeScheduleId is required*");
        _contractRepo.Verify(r => r.UpdateAsync(It.IsAny<ProviderContract>()), Times.Never);
    }

    [Fact]
    public async Task ReplaceTerms_RejectsATermNoClaimLineCouldReach()
    {
        Returns(Contract());

        // The default term first: it covers every line for all time, so the
        // carve-out behind it would never price one.
        var result = await _controller.ReplaceReimbursementTerms("c-1", new ReimbursementTermsRequest
        {
            Terms = [DefaultTerm(), CarveOutTerm()],
        });

        Details(result).Should().ContainMatch("term 2 is unreachable*term 1*");
    }

    [Fact]
    public async Task ReplaceTerms_AllowsALaterTermOutsideTheEarlierTermsDates()
    {
        var contract = Contract();
        Returns(contract);

        var original = DefaultTerm();
        original.TermDate = new DateTime(2026, 5, 31);

        var amended = DefaultTerm();
        amended.Id = "term-amended";
        amended.FeeScheduleId = "comm-2027";
        amended.EffectiveDate = new DateTime(2026, 6, 1);

        var result = await _controller.ReplaceReimbursementTerms(
            "c-1", new ReimbursementTermsRequest { Terms = [original, amended] });

        Ok(result).Should().HaveCount(2);
    }

    [Fact]
    public async Task ReplaceTerms_RejectsADuplicateTermId()
    {
        Returns(Contract());

        var first = DefaultTerm();
        var second = CarveOutTerm();
        second.Id = first.Id;

        var result = await _controller.ReplaceReimbursementTerms(
            "c-1", new ReimbursementTermsRequest { Terms = [second, first] });

        Details(result).Should().ContainMatch("term 2: id * is already used*");
    }

    [Fact]
    public async Task ReplaceTerms_OnAnUnknownContract_IsNotFound()
    {
        _contractRepo.Setup(r => r.GetByIdAsync("nope")).ReturnsAsync((ProviderContract?)null);

        var result = await _controller.ReplaceReimbursementTerms(
            "nope", new ReimbursementTermsRequest { Terms = [DefaultTerm()] });

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetTerms_ReturnsTheStoredSheet()
    {
        var contract = Contract();
        contract.ReimbursementTerms = [CarveOutTerm(), DefaultTerm()];
        Returns(contract);

        var result = await _controller.GetReimbursementTerms("c-1");

        Ok(result).Select(t => t.FeeScheduleId).Should().Equal("ortho-2026", "comm-2026");
    }

    [Fact]
    public async Task UpdateContract_CannotClearTheRateSheet()
    {
        var existing = Contract();
        existing.ReimbursementTerms = [DefaultTerm()];
        Returns(existing);

        // What the portal sends: the contract fields, and no terms.
        var edit = Contract();
        edit.ProviderName = "Dr. Chen (renamed)";

        await _controller.UpdateContract("c-1", edit);

        edit.ReimbursementTerms.Should().HaveCount(1);
        edit.ReimbursementTerms.Single().FeeScheduleId.Should().Be("comm-2026");
    }

    // ── The ffs-service fold ──────────────────────────────────────────

    [Fact]
    public async Task AddFfsRateConfig_BecomesAFeeScheduleTermOnTheContract()
    {
        var contract = Contract(PaymentMethodology.FeeForService);
        Returns(contract);

        var result = await _controller.AddFfsRateConfigTerm("c-1", new FfsRateConfigTermRequest
        {
            FeeScheduleId = "mpfs-2026",
            FeeSchedulePercentage = 1.15m,
            MultipleProcedureReduction = false,
            BilateralAdjustment = true,
            Description = "115% of Medicare",
        });

        var term = Ok(result).Single();
        term.Method.Should().Be(EngineModels.ReimbursementMethod.FeeScheduleRef);
        term.FeeScheduleId.Should().Be("mpfs-2026");
        term.Multiplier.Should().Be(1.15m);
        term.ModifierRules!.MultipleProcedureReduction.Should().BeFalse();
        term.ModifierRules!.BilateralAdjustment.Should().BeTrue();
        term.Description.Should().Be("115% of Medicare");
        term.Validate().Should().BeEmpty();
    }

    [Fact]
    public async Task AddFfsRateConfig_CarriesItsProcedureRangeAndDates()
    {
        var contract = Contract(PaymentMethodology.Hybrid);
        Returns(contract);

        var result = await _controller.AddFfsRateConfigTerm("c-1", new FfsRateConfigTermRequest
        {
            FeeScheduleId = "ortho-2026",
            ProcedureCodeFrom = "27400",
            ProcedureCodeTo = "27599",
            EffectiveDate = new DateTime(2026, 6, 1),
            TerminationDate = new DateTime(2026, 12, 31),
        });

        var term = Ok(result).Single();
        term.Scope.ProcedureCodeFrom.Should().Be("27400");
        term.Scope.ProcedureCodeTo.Should().Be("27599");
        term.EffectiveDate.Should().Be(new DateTime(2026, 6, 1));
        term.TermDate.Should().Be(new DateTime(2026, 12, 31));
    }

    [Fact]
    public async Task AddFfsRateConfig_OnAFullyCapitatedContract_IsRejected()
    {
        Returns(Contract(PaymentMethodology.FullCapitation));

        var result = await _controller.AddFfsRateConfigTerm(
            "c-1", new FfsRateConfigTermRequest { FeeScheduleId = "mpfs-2026" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _contractRepo.Verify(r => r.UpdateAsync(It.IsAny<ProviderContract>()), Times.Never);
    }

    [Fact]
    public async Task AddFfsRateConfig_BehindADefaultTerm_IsRejectedAsUnreachable()
    {
        var contract = Contract(PaymentMethodology.FeeForService);
        contract.ReimbursementTerms = [DefaultTerm()];
        Returns(contract);

        var result = await _controller.AddFfsRateConfigTerm(
            "c-1", new FfsRateConfigTermRequest { FeeScheduleId = "mpfs-2026" });

        Details(result).Should().ContainMatch("term 2 is unreachable*");
    }

    // ── The pricing contract ──────────────────────────────────────────

    [Fact]
    public async Task PricingContract_IsOneEngineContractPerPlan_CarryingTheTerms()
    {
        var contract = Contract(PaymentMethodology.FeeForService, ProviderContractStatus.Active);
        contract.PlanIds = ["plan-a", "plan-b"];
        contract.ReimbursementTerms = [CarveOutTerm(), DefaultTerm()];
        Returns(contract);

        var projection = Projection(await _controller.GetPricingContract("c-1"));

        projection.Warnings.Should().BeEmpty();
        projection.Contracts.Should().HaveCount(2);
        projection.Contracts.Select(c => c.PlanId).Should().Equal("plan-a", "plan-b");

        var first = projection.Contracts[0];
        first.Id.Should().Be("tenant-1:1234567890:plan-a");
        first.ProviderNpi.Should().Be("1234567890");
        first.NetworkStatus.Should().Be(EngineDomain.NetworkStatus.InNetwork);
        first.EffectiveDate.Should().Be(contract.EffectiveDate);
        first.ReimbursementTerms.Should().HaveCount(2);
        first.LegalContractId.Should().Be("c-1");
        first.ContractNumber.Should().Be(contract.ContractNumber);
        first.LineOfBusiness.Should().Be("Commercial");
        first.PlanIds.Should().Equal("plan-a", "plan-b");
        first.Origin.Should().Be("legal");

        // The terms are the rate sheet, so the legacy fields stay empty.
        first.FeeScheduleId.Should().BeEmpty();
        first.ContractLines.Should().BeEmpty();
    }

    [Fact]
    public async Task PricingContract_CarriesNoTin()
    {
        var contract = Contract(PaymentMethodology.FeeForService, ProviderContractStatus.Active);
        contract.PlanIds = ["plan-a"];
        contract.ReimbursementTerms = [DefaultTerm()];
        Returns(contract);

        var projection = Projection(await _controller.GetPricingContract("c-1"));

        projection.Contracts.Single().GroupTin.Should().BeNull();
    }

    [Fact]
    public async Task PricingContract_WarnsWhenTheContractIsNotActive()
    {
        var contract = Contract(PaymentMethodology.FeeForService, ProviderContractStatus.Draft);
        contract.PlanIds = ["plan-a"];
        contract.ReimbursementTerms = [DefaultTerm()];
        Returns(contract);

        Projection(await _controller.GetPricingContract("c-1"))
            .Warnings.Should().ContainMatch("*Draft, not Active*");
    }

    [Fact]
    public async Task PricingContract_WarnsWhenTheContractNamesNoTerms()
    {
        var contract = Contract(PaymentMethodology.FeeForService, ProviderContractStatus.Active);
        contract.PlanIds = ["plan-a"];
        Returns(contract);

        Projection(await _controller.GetPricingContract("c-1"))
            .Warnings.Should().ContainMatch("*names no reimbursement terms*");
    }

    [Fact]
    public async Task PricingContract_WithNoPlanIds_ProjectsOneContractAndSaysItMustBeExpanded()
    {
        var contract = Contract(PaymentMethodology.FeeForService, ProviderContractStatus.Active);
        contract.ReimbursementTerms = [DefaultTerm()];
        Returns(contract);

        var projection = Projection(await _controller.GetPricingContract("c-1"));

        projection.Warnings.Should().ContainMatch("*names no plan ids*plan catalogue*");
        projection.Contracts.Single().PlanId.Should().BeEmpty();
    }

    [Theory]
    [InlineData(NetworkParticipationStatus.Participating, EngineDomain.NetworkStatus.InNetwork, false)]
    [InlineData(NetworkParticipationStatus.NonParticipating, EngineDomain.NetworkStatus.OutOfNetwork, false)]
    [InlineData(NetworkParticipationStatus.TieredException, EngineDomain.NetworkStatus.InNetwork, true)]
    public async Task PricingContract_MapsNetworkParticipation(
        NetworkParticipationStatus participation, EngineDomain.NetworkStatus expected, bool warns)
    {
        var contract = Contract(PaymentMethodology.FeeForService, ProviderContractStatus.Active);
        contract.PlanIds = ["plan-a"];
        contract.NetworkStatus = participation;
        contract.ReimbursementTerms = [DefaultTerm()];
        Returns(contract);

        var projection = Projection(await _controller.GetPricingContract("c-1"));

        projection.Contracts.Single().NetworkStatus.Should().Be(expected);
        if (warns)
            projection.Warnings.Should().ContainMatch("*TieredException*");
        else
            projection.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task PricingContract_OnAnUnknownContract_IsNotFound()
    {
        _contractRepo.Setup(r => r.GetByIdAsync("nope")).ReturnsAsync((ProviderContract?)null);

        (await _controller.GetPricingContract("nope")).Result
            .Should().BeOfType<NotFoundObjectResult>();
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private void Returns(ProviderContract contract)
    {
        _contractRepo.Setup(r => r.GetByIdAsync(contract.Id)).ReturnsAsync(contract);
        _contractRepo.Setup(r => r.UpdateAsync(It.IsAny<ProviderContract>()))
            .ReturnsAsync((ProviderContract c) => c);
    }

    private static ProviderContract Contract(
        PaymentMethodology methodology = PaymentMethodology.FeeForService,
        ProviderContractStatus status = ProviderContractStatus.Draft) => new()
    {
        Id = "c-1",
        TenantId = "tenant-1",
        ContractNumber = "CTR-1234567890-2026",
        ProviderNPI = "1234567890",
        ProviderName = "Dr. Chen",
        ProviderTin = "123456789",
        ProviderType = ProviderType.Individual,
        LineOfBusiness = LineOfBusiness.Commercial,
        PaymentMethodology = methodology,
        NetworkStatus = NetworkParticipationStatus.Participating,
        Status = status,
        EffectiveDate = new DateTime(2026, 1, 1),
    };

    private static EngineModels.ReimbursementTerm DefaultTerm() => new()
    {
        Id = "term-default",
        Method = EngineModels.ReimbursementMethod.FeeScheduleRef,
        FeeScheduleId = "comm-2026",
    };

    private static EngineModels.ReimbursementTerm CarveOutTerm() => new()
    {
        Id = "term-ortho",
        Method = EngineModels.ReimbursementMethod.FeeScheduleRef,
        FeeScheduleId = "ortho-2026",
        Scope = new EngineModels.ReimbursementTermScope
        {
            ProcedureCodeFrom = "27400",
            ProcedureCodeTo = "27599",
        },
    };

    private static List<EngineModels.ReimbursementTerm> Ok(
        ActionResult<IEnumerable<EngineModels.ReimbursementTerm>> result)
    {
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return (ok.Value as IEnumerable<EngineModels.ReimbursementTerm>)!.ToList();
    }

    private static PricingContractProjectionResult Projection(
        ActionResult<PricingContractProjectionResult> result)
        => result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PricingContractProjectionResult>().Subject;

    /// <summary>The validation messages from a 400, so a test can name the rule it is checking.</summary>
    private static List<string> Details(ActionResult<IEnumerable<EngineModels.ReimbursementTerm>> result)
    {
        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var details = bad.Value!.GetType().GetProperty("details")!.GetValue(bad.Value);
        return ((IEnumerable<string>)details!).ToList();
    }
}
