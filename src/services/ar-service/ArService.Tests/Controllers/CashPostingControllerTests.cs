using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ArService.Controllers;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Tests.Controllers;

public class CashPostingControllerTests
{
    private readonly Mock<ICashPostingRepository> _cashPostingRepo;
    private readonly CashPostingController _controller;

    public CashPostingControllerTests()
    {
        _cashPostingRepo = new Mock<ICashPostingRepository>();
        var logger = new Mock<ILogger<CashPostingController>>();
        _controller = new CashPostingController(_cashPostingRepo.Object, logger.Object);
    }

    private static CashPosting CreatePosting(
        string id = "cp-1",
        decimal amount = 15000.00m,
        CashPostingStatus status = CashPostingStatus.Pending,
        string postingNumber = "CP-20260301-ABCD1234",
        List<CashApplication>? applications = null) => new()
    {
        Id = id,
        TenantId = "tenant-1",
        PostingNumber = postingNumber,
        ReceiptDate = new DateTime(2026, 3, 1),
        Amount = amount,
        PaymentMethod = PaymentMethod.Eft,
        PayerType = PayerType.Sponsor,
        PayerReferenceId = "sponsor-1",
        PayerName = "Acme Health Group",
        Status = status,
        Applications = applications ?? new List<CashApplication>(),
        CreatedBy = "billing-user"
    };

    private static CashApplication CreateApplication(
        decimal amountApplied = 5000m,
        string glAccountId = "acct-1",
        string arBalanceId = "bal-1") => new()
    {
        GlAccountId = glAccountId,
        ArBalanceId = arBalanceId,
        Period = new DateTime(2026, 3, 1),
        AmountApplied = amountApplied,
        Memo = "Premium payment"
    };

    #region SearchCashPostings

    [Fact]
    public async Task SearchCashPostings_NoFilters_ReturnsAll()
    {
        var postings = new List<CashPosting> { CreatePosting(), CreatePosting("cp-2") };
        _cashPostingRepo.Setup(r => r.SearchAsync(null, null, null, null, 1, 50)).ReturnsAsync(postings);

        var result = await _controller.SearchCashPostings();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<CashPosting>)!.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchCashPostings_WithFilters_PassesThrough()
    {
        var dateFrom = new DateTime(2026, 1, 1);
        var dateTo = new DateTime(2026, 3, 31);
        _cashPostingRepo.Setup(r => r.SearchAsync(PayerType.Sponsor, CashPostingStatus.Pending,
            dateFrom, dateTo, 1, 50))
            .ReturnsAsync(new List<CashPosting>());

        await _controller.SearchCashPostings(
            payerType: PayerType.Sponsor, status: CashPostingStatus.Pending,
            dateFrom: dateFrom, dateTo: dateTo);

        _cashPostingRepo.Verify(r => r.SearchAsync(PayerType.Sponsor, CashPostingStatus.Pending,
            dateFrom, dateTo, 1, 50), Times.Once);
    }

    #endregion

    #region GetCashPostingById

    [Fact]
    public async Task GetCashPostingById_Found_ReturnsOk()
    {
        _cashPostingRepo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(CreatePosting());

        var result = await _controller.GetCashPostingById("cp-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CashPosting)!.Amount.Should().Be(15000.00m);
    }

    [Fact]
    public async Task GetCashPostingById_NotFound_Returns404()
    {
        _cashPostingRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((CashPosting?)null);

        var result = await _controller.GetCashPostingById("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region CreateCashPosting

    [Fact]
    public async Task CreateCashPosting_AutoGeneratesPostingNumber()
    {
        var posting = CreatePosting();
        posting.PostingNumber = ""; // Will be overwritten
        _cashPostingRepo.Setup(r => r.CreateAsync(It.IsAny<CashPosting>()))
            .ReturnsAsync((CashPosting p) => p);

        var result = await _controller.CreateCashPosting(posting);

        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        var savedPosting = created!.Value as CashPosting;
        savedPosting!.PostingNumber.Should().StartWith("CP-");
        savedPosting.PostingNumber.Should().MatchRegex(@"^CP-\d{8}-[A-Z0-9]{8}$");
    }

    [Fact]
    public async Task CreateCashPosting_ForcesStatusPending()
    {
        var posting = CreatePosting(status: CashPostingStatus.Applied);
        _cashPostingRepo.Setup(r => r.CreateAsync(It.IsAny<CashPosting>()))
            .ReturnsAsync((CashPosting p) => p);

        var result = await _controller.CreateCashPosting(posting);

        var created = result.Result as CreatedAtActionResult;
        (created!.Value as CashPosting)!.Status.Should().Be(CashPostingStatus.Pending);
    }

    [Fact]
    public async Task CreateCashPosting_Returns201WithCorrectRouteValues()
    {
        var posting = CreatePosting();
        _cashPostingRepo.Setup(r => r.CreateAsync(It.IsAny<CashPosting>()))
            .ReturnsAsync((CashPosting p) => p);

        var result = await _controller.CreateCashPosting(posting);

        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        created!.StatusCode.Should().Be(201);
        created.ActionName.Should().Be(nameof(CashPostingController.GetCashPostingById));
    }

    #endregion

    #region ApplyCashPosting

    [Fact]
    public async Task ApplyCashPosting_ComputesAppliedAmountFromApplications()
    {
        var applications = new List<CashApplication>
        {
            CreateApplication(amountApplied: 5000.00m, arBalanceId: "bal-1"),
            CreateApplication(amountApplied: 3000.50m, arBalanceId: "bal-2"),
            CreateApplication(amountApplied: 2000.25m, arBalanceId: "bal-3")
        };
        var posting = CreatePosting(amount: 15000.00m, status: CashPostingStatus.Pending,
            applications: applications);
        _cashPostingRepo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        _cashPostingRepo.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>()))
            .ReturnsAsync((CashPosting p) => p);

        var result = await _controller.ApplyCashPosting("cp-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var applied = ok!.Value as CashPosting;
        applied!.AppliedAmount.Should().Be(5000.00m + 3000.50m + 2000.25m); // 10000.75
        applied.UnappliedAmount.Should().Be(15000.00m - 10000.75m);          // 4999.25
        applied.Status.Should().Be(CashPostingStatus.PartiallyApplied);      // Partial — not fully applied
    }

    [Fact]
    public async Task ApplyCashPosting_UnappliedAmount_EqualsAmountMinusApplied()
    {
        // CRITICAL: UnappliedAmount = Amount - AppliedAmount must be exact
        var applications = new List<CashApplication>
        {
            CreateApplication(amountApplied: 7500.00m),
            CreateApplication(amountApplied: 7500.00m)
        };
        var posting = CreatePosting(amount: 15000.00m, status: CashPostingStatus.Pending,
            applications: applications);
        _cashPostingRepo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        _cashPostingRepo.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>()))
            .ReturnsAsync((CashPosting p) => p);

        var result = await _controller.ApplyCashPosting("cp-1");

        var ok = result.Result as OkObjectResult;
        var applied = ok!.Value as CashPosting;
        applied!.AppliedAmount.Should().Be(15000.00m);
        applied.UnappliedAmount.Should().Be(0m); // Fully applied
    }

    [Fact]
    public async Task ApplyCashPosting_VoidedPosting_ReturnsBadRequest()
    {
        var posting = CreatePosting(status: CashPostingStatus.Voided);
        _cashPostingRepo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);

        var result = await _controller.ApplyCashPosting("cp-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ApplyCashPosting_NotFound_Returns404()
    {
        _cashPostingRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((CashPosting?)null);

        var result = await _controller.ApplyCashPosting("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ApplyCashPosting_NoApplications_AppliedAmountIsZero()
    {
        var posting = CreatePosting(amount: 15000.00m, status: CashPostingStatus.Pending);
        posting.Applications = new List<CashApplication>(); // Empty
        _cashPostingRepo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        _cashPostingRepo.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>()))
            .ReturnsAsync((CashPosting p) => p);

        var result = await _controller.ApplyCashPosting("cp-1");

        var ok = result.Result as OkObjectResult;
        var applied = ok!.Value as CashPosting;
        applied!.AppliedAmount.Should().Be(0m);
        applied.UnappliedAmount.Should().Be(15000.00m); // Full amount unapplied
    }

    [Fact]
    public async Task ApplyCashPosting_SetsLastUpdatedAt()
    {
        var posting = CreatePosting(status: CashPostingStatus.Pending);
        posting.LastUpdatedAt = new DateTime(2026, 1, 1);
        _cashPostingRepo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        _cashPostingRepo.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>()))
            .ReturnsAsync((CashPosting p) => p);

        var before = DateTime.UtcNow;
        var result = await _controller.ApplyCashPosting("cp-1");

        var ok = result.Result as OkObjectResult;
        (ok!.Value as CashPosting)!.LastUpdatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task ApplyCashPosting_DecimalPrecision_PennyLevelAccuracy()
    {
        // Financial scenario: many small applications that must sum precisely
        var applications = new List<CashApplication>
        {
            CreateApplication(amountApplied: 33.33m),
            CreateApplication(amountApplied: 33.33m),
            CreateApplication(amountApplied: 33.34m)
        };
        var posting = CreatePosting(amount: 100.00m, status: CashPostingStatus.Pending,
            applications: applications);
        _cashPostingRepo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        _cashPostingRepo.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>()))
            .ReturnsAsync((CashPosting p) => p);

        var result = await _controller.ApplyCashPosting("cp-1");

        var ok = result.Result as OkObjectResult;
        var applied = ok!.Value as CashPosting;
        applied!.AppliedAmount.Should().Be(100.00m);
        applied.UnappliedAmount.Should().Be(0.00m);
    }

    #endregion

    #region VoidCashPosting

    [Fact]
    public async Task VoidCashPosting_PendingPosting_SetsStatusVoided()
    {
        var posting = CreatePosting(status: CashPostingStatus.Pending);
        _cashPostingRepo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        _cashPostingRepo.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>()))
            .ReturnsAsync((CashPosting p) => p);

        var result = await _controller.VoidCashPosting("cp-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as CashPosting)!.Status.Should().Be(CashPostingStatus.Voided);
    }

    [Fact]
    public async Task VoidCashPosting_AlreadyVoided_ReturnsBadRequest()
    {
        var posting = CreatePosting(status: CashPostingStatus.Voided);
        _cashPostingRepo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);

        var result = await _controller.VoidCashPosting("cp-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task VoidCashPosting_NotFound_Returns404()
    {
        _cashPostingRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((CashPosting?)null);

        var result = await _controller.VoidCashPosting("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task VoidCashPosting_AppliedPosting_ReturnsBadRequest()
    {
        // Cannot void applied postings — must reverse the application first
        var posting = CreatePosting(status: CashPostingStatus.Applied);
        _cashPostingRepo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);

        var result = await _controller.VoidCashPosting("cp-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task VoidCashPosting_SetsLastUpdatedAt()
    {
        var posting = CreatePosting(status: CashPostingStatus.Pending);
        posting.LastUpdatedAt = new DateTime(2026, 1, 1);
        _cashPostingRepo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        _cashPostingRepo.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>()))
            .ReturnsAsync((CashPosting p) => p);

        var before = DateTime.UtcNow;
        var result = await _controller.VoidCashPosting("cp-1");

        var ok = result.Result as OkObjectResult;
        (ok!.Value as CashPosting)!.LastUpdatedAt.Should().BeOnOrAfter(before);
    }

    #endregion
}
