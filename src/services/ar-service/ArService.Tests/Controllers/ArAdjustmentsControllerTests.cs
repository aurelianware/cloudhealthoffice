using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ArService.Controllers;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Tests.Controllers;

public class ArAdjustmentsControllerTests
{
    private readonly Mock<IArAdjustmentRepository> _adjustmentRepo;
    private readonly Mock<IArBalanceRepository> _balanceRepo;
    private readonly ArAdjustmentsController _controller;

    public ArAdjustmentsControllerTests()
    {
        _adjustmentRepo = new Mock<IArAdjustmentRepository>();
        _balanceRepo = new Mock<IArBalanceRepository>();
        var logger = new Mock<ILogger<ArAdjustmentsController>>();
        _controller = new ArAdjustmentsController(_adjustmentRepo.Object, _balanceRepo.Object, logger.Object);
    }

    private static ArAdjustment CreateAdjustment(
        string id = "adj-1",
        ArAdjustmentStatus status = ArAdjustmentStatus.Pending,
        decimal amount = 5000.00m,
        ArAdjustmentDirection direction = ArAdjustmentDirection.Debit,
        ArAdjustmentType adjustmentType = ArAdjustmentType.ManualCorrection,
        string arBalanceId = "bal-1",
        string? authorizedBy = null) => new()
    {
        Id = id,
        TenantId = "tenant-1",
        AdjustmentNumber = $"ADJ-20260301-TEST0001",
        AdjustmentType = adjustmentType,
        GlAccountId = "acct-1",
        ArBalanceId = arBalanceId,
        Period = new DateTime(2026, 3, 1),
        Amount = amount,
        Direction = direction,
        ReasonCode = "MC-001",
        Narrative = "Manual correction for billing error",
        Status = status,
        AuthorizedBy = authorizedBy,
        CreatedBy = "finance-user"
    };

    private static ArBalance CreateBalance(
        string id = "bal-1",
        decimal openingBalance = 10000m,
        decimal totalDebits = 5000m,
        decimal totalCredits = 2000m) => new()
    {
        Id = id,
        TenantId = "tenant-1",
        GlAccountId = "acct-1",
        AccountNumber = "4010",
        Period = new DateTime(2026, 3, 1),
        OpeningBalance = openingBalance,
        TotalDebits = totalDebits,
        TotalCredits = totalCredits,
        ClosingBalance = openingBalance + totalDebits - totalCredits
    };

    #region SearchAdjustments

    [Fact]
    public async Task SearchAdjustments_NoFilters_ReturnsAll()
    {
        var adjustments = new List<ArAdjustment> { CreateAdjustment(), CreateAdjustment("adj-2") };
        _adjustmentRepo.Setup(r => r.SearchAsync(null, null, null, null, 1, 50)).ReturnsAsync(adjustments);

        var result = await _controller.SearchAdjustments();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<ArAdjustment>)!.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAdjustments_WithFilters_PassesThrough()
    {
        var period = new DateTime(2026, 3, 1);
        _adjustmentRepo.Setup(r => r.SearchAsync(ArAdjustmentType.WriteOff, ArAdjustmentStatus.Pending,
            period, "acct-1", 1, 50))
            .ReturnsAsync(new List<ArAdjustment>());

        await _controller.SearchAdjustments(
            type: ArAdjustmentType.WriteOff, status: ArAdjustmentStatus.Pending,
            period: period, glAccountId: "acct-1");

        _adjustmentRepo.Verify(r => r.SearchAsync(ArAdjustmentType.WriteOff, ArAdjustmentStatus.Pending,
            period, "acct-1", 1, 50), Times.Once);
    }

    #endregion

    #region GetAdjustmentById

    [Fact]
    public async Task GetAdjustmentById_Found_ReturnsOk()
    {
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(CreateAdjustment());

        var result = await _controller.GetAdjustmentById("adj-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as ArAdjustment)!.Amount.Should().Be(5000.00m);
    }

    [Fact]
    public async Task GetAdjustmentById_NotFound_Returns404()
    {
        _adjustmentRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ArAdjustment?)null);

        var result = await _controller.GetAdjustmentById("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region CreateAdjustment

    [Fact]
    public async Task CreateAdjustment_AutoGeneratesAdjustmentNumber()
    {
        var adjustment = CreateAdjustment();
        adjustment.AdjustmentNumber = ""; // Will be overwritten
        _adjustmentRepo.Setup(r => r.CreateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var result = await _controller.CreateAdjustment(adjustment);

        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        var saved = created!.Value as ArAdjustment;
        saved!.AdjustmentNumber.Should().StartWith("ADJ-");
        saved.AdjustmentNumber.Should().MatchRegex(@"^ADJ-\d{8}-[A-Z0-9]{8}$");
    }

    [Fact]
    public async Task CreateAdjustment_ForcesStatusPending()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Approved);
        _adjustmentRepo.Setup(r => r.CreateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var result = await _controller.CreateAdjustment(adjustment);

        var created = result.Result as CreatedAtActionResult;
        (created!.Value as ArAdjustment)!.Status.Should().Be(ArAdjustmentStatus.Pending);
    }

    [Fact]
    public async Task CreateAdjustment_Returns201()
    {
        var adjustment = CreateAdjustment();
        _adjustmentRepo.Setup(r => r.CreateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var result = await _controller.CreateAdjustment(adjustment);

        var created = result.Result as CreatedAtActionResult;
        created!.StatusCode.Should().Be(201);
    }

    #endregion

    #region ApproveAdjustment

    [Fact]
    public async Task ApproveAdjustment_PendingStatus_SetsApprovedWithAuthorization()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Pending);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        _adjustmentRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var before = DateTime.UtcNow;
        var request = new ApproveAdjustmentRequest { AuthorizedBy = "supervisor" };
        var result = await _controller.ApproveAdjustment("adj-1", request);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var approved = ok!.Value as ArAdjustment;
        approved!.Status.Should().Be(ArAdjustmentStatus.Approved);
        approved.AuthorizedBy.Should().Be("supervisor");
        approved.AuthorizedAt.Should().NotBeNull();
        approved.AuthorizedAt!.Value.Should().BeOnOrAfter(before);
        approved.LastUpdatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task ApproveAdjustment_AlreadyApproved_ReturnsBadRequest()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Approved);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);

        var result = await _controller.ApproveAdjustment("adj-1",
            new ApproveAdjustmentRequest { AuthorizedBy = "supervisor" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ApproveAdjustment_PostedStatus_ReturnsBadRequest()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Posted);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);

        var result = await _controller.ApproveAdjustment("adj-1",
            new ApproveAdjustmentRequest { AuthorizedBy = "supervisor" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ApproveAdjustment_RejectedStatus_ReturnsBadRequest()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Rejected);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);

        var result = await _controller.ApproveAdjustment("adj-1",
            new ApproveAdjustmentRequest { AuthorizedBy = "supervisor" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ApproveAdjustment_NotFound_Returns404()
    {
        _adjustmentRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ArAdjustment?)null);

        var result = await _controller.ApproveAdjustment("missing",
            new ApproveAdjustmentRequest { AuthorizedBy = "supervisor" });

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region RejectAdjustment

    [Fact]
    public async Task RejectAdjustment_PendingStatus_SetsRejected()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Pending);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        _adjustmentRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var request = new RejectAdjustmentRequest { Reason = "Duplicate entry" };
        var result = await _controller.RejectAdjustment("adj-1", request);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var rejected = ok!.Value as ArAdjustment;
        rejected!.Status.Should().Be(ArAdjustmentStatus.Rejected);
        rejected.Narrative.Should().Be("Duplicate entry");
    }

    [Fact]
    public async Task RejectAdjustment_NonPendingStatus_ReturnsBadRequest()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Approved);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);

        var result = await _controller.RejectAdjustment("adj-1",
            new RejectAdjustmentRequest { Reason = "Too late" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task RejectAdjustment_NotFound_Returns404()
    {
        _adjustmentRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ArAdjustment?)null);

        var result = await _controller.RejectAdjustment("missing",
            new RejectAdjustmentRequest { Reason = "test" });

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region PostAdjustment

    [Fact]
    public async Task PostAdjustment_ApprovedDebit_PostsCorrectlyToBalance()
    {
        // CRITICAL: Debit adjustment adds to TotalDebits, recalculates ClosingBalance
        var adjustment = CreateAdjustment(
            status: ArAdjustmentStatus.Approved,
            amount: 3000.00m,
            direction: ArAdjustmentDirection.Debit,
            authorizedBy: "supervisor");

        var balance = CreateBalance(openingBalance: 10000m, totalDebits: 5000m, totalCredits: 2000m);
        // ClosingBalance = 10000 + 5000 - 2000 = 13000

        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        _balanceRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);
        _adjustmentRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);
        _balanceRepo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>()))
            .ReturnsAsync((ArBalance b) => b);

        var result = await _controller.PostAdjustment("adj-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var posted = ok!.Value as ArAdjustment;
        posted!.Status.Should().Be(ArAdjustmentStatus.Posted);

        // Verify balance was updated correctly
        _balanceRepo.Verify(r => r.UpdateAsync(It.Is<ArBalance>(b =>
            b.TotalDebits == 5000m + 3000m &&           // 8000
            b.TotalCredits == 2000m &&                    // unchanged
            b.ClosingBalance == 10000m + 8000m - 2000m   // 16000
        )), Times.Once);
    }

    [Fact]
    public async Task PostAdjustment_ApprovedCredit_PostsCorrectlyToBalance()
    {
        // CRITICAL: Credit adjustment adds to TotalCredits, recalculates ClosingBalance
        var adjustment = CreateAdjustment(
            status: ArAdjustmentStatus.Approved,
            amount: 1500.00m,
            direction: ArAdjustmentDirection.Credit,
            authorizedBy: "supervisor");

        var balance = CreateBalance(openingBalance: 10000m, totalDebits: 5000m, totalCredits: 2000m);
        // ClosingBalance = 10000 + 5000 - 2000 = 13000

        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        _balanceRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);
        _adjustmentRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);
        _balanceRepo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>()))
            .ReturnsAsync((ArBalance b) => b);

        var result = await _controller.PostAdjustment("adj-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();

        // Verify balance was updated correctly
        _balanceRepo.Verify(r => r.UpdateAsync(It.Is<ArBalance>(b =>
            b.TotalDebits == 5000m &&                      // unchanged
            b.TotalCredits == 2000m + 1500m &&             // 3500
            b.ClosingBalance == 10000m + 5000m - 3500m     // 11500
        )), Times.Once);
    }

    [Fact]
    public async Task PostAdjustment_CreatesPostingEntryOnBalance()
    {
        var adjustment = CreateAdjustment(
            status: ArAdjustmentStatus.Approved,
            amount: 2500.00m,
            direction: ArAdjustmentDirection.Debit,
            authorizedBy: "supervisor");

        var balance = CreateBalance();
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        _balanceRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);
        _adjustmentRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);
        _balanceRepo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>()))
            .ReturnsAsync((ArBalance b) => b);

        await _controller.PostAdjustment("adj-1");

        _balanceRepo.Verify(r => r.UpdateAsync(It.Is<ArBalance>(b =>
            b.PostingEntries.Count == 1 &&
            b.PostingEntries[0].Source == ArPostingSource.ManualAdjustment &&
            b.PostingEntries[0].SourceReferenceId == "adj-1" &&
            b.PostingEntries[0].DebitAmount == 2500.00m &&
            b.PostingEntries[0].CreditAmount == 0m &&
            b.PostingEntries[0].PostedBy == "supervisor"
        )), Times.Once);
    }

    [Fact]
    public async Task PostAdjustment_NonApprovedStatus_ReturnsBadRequest()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Pending);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);

        var result = await _controller.PostAdjustment("adj-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PostAdjustment_ReversedStatus_ReturnsBadRequest()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Reversed);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);

        var result = await _controller.PostAdjustment("adj-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PostAdjustment_BalanceNotFound_ReturnsBadRequest()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Approved);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        _balanceRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync((ArBalance?)null);

        var result = await _controller.PostAdjustment("adj-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PostAdjustment_NotFound_Returns404()
    {
        _adjustmentRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ArAdjustment?)null);

        var result = await _controller.PostAdjustment("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region ReverseAdjustment

    [Fact]
    public async Task ReverseAdjustment_PostedStatus_SetsReversed()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Posted);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        _adjustmentRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var result = await _controller.ReverseAdjustment("adj-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as ArAdjustment)!.Status.Should().Be(ArAdjustmentStatus.Reversed);
    }

    [Fact]
    public async Task ReverseAdjustment_PendingStatus_ReturnsBadRequest()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Pending);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);

        var result = await _controller.ReverseAdjustment("adj-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ReverseAdjustment_ApprovedStatus_ReturnsBadRequest()
    {
        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Approved);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);

        var result = await _controller.ReverseAdjustment("adj-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ReverseAdjustment_NotFound_Returns404()
    {
        _adjustmentRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ArAdjustment?)null);

        var result = await _controller.ReverseAdjustment("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region FullLifecycle

    [Fact]
    public async Task FullLifecycle_Create_Approve_Post_Reverse()
    {
        // Track state across the entire adjustment lifecycle
        var adjustment = CreateAdjustment(
            status: ArAdjustmentStatus.Pending,
            amount: 7500.00m,
            direction: ArAdjustmentDirection.Debit);

        var balance = CreateBalance(openingBalance: 20000m, totalDebits: 0m, totalCredits: 0m);

        // Step 1: Create
        _adjustmentRepo.Setup(r => r.CreateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var createResult = await _controller.CreateAdjustment(adjustment);
        var created = (createResult.Result as CreatedAtActionResult)!.Value as ArAdjustment;
        created!.Status.Should().Be(ArAdjustmentStatus.Pending);
        created.AdjustmentNumber.Should().StartWith("ADJ-");

        // Step 2: Approve
        _adjustmentRepo.Setup(r => r.GetByIdAsync(created.Id)).ReturnsAsync(created);
        _adjustmentRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var approveResult = await _controller.ApproveAdjustment(created.Id,
            new ApproveAdjustmentRequest { AuthorizedBy = "cfo" });
        var approved = (approveResult.Result as OkObjectResult)!.Value as ArAdjustment;
        approved!.Status.Should().Be(ArAdjustmentStatus.Approved);
        approved.AuthorizedBy.Should().Be("cfo");

        // Step 3: Post
        _adjustmentRepo.Setup(r => r.GetByIdAsync(approved.Id)).ReturnsAsync(approved);
        _balanceRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);
        _balanceRepo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>()))
            .ReturnsAsync((ArBalance b) => b);

        var postResult = await _controller.PostAdjustment(approved.Id);
        var posted = (postResult.Result as OkObjectResult)!.Value as ArAdjustment;
        posted!.Status.Should().Be(ArAdjustmentStatus.Posted);

        // Verify balance was impacted (debit of 7500)
        _balanceRepo.Verify(r => r.UpdateAsync(It.Is<ArBalance>(b =>
            b.TotalDebits == 7500m &&
            b.ClosingBalance == 20000m + 7500m - 0m // 27500
        )), Times.Once);

        // Step 4: Reverse
        _adjustmentRepo.Setup(r => r.GetByIdAsync(posted.Id)).ReturnsAsync(posted);

        var reverseResult = await _controller.ReverseAdjustment(posted.Id);
        var reversed = (reverseResult.Result as OkObjectResult)!.Value as ArAdjustment;
        reversed!.Status.Should().Be(ArAdjustmentStatus.Reversed);
    }

    #endregion

    #region InvalidStateTransitions

    [Theory]
    [InlineData(ArAdjustmentStatus.Approved)]
    [InlineData(ArAdjustmentStatus.Posted)]
    [InlineData(ArAdjustmentStatus.Reversed)]
    [InlineData(ArAdjustmentStatus.Rejected)]
    public async Task ApproveAdjustment_AllNonPendingStates_ReturnsBadRequest(ArAdjustmentStatus status)
    {
        var adjustment = CreateAdjustment(status: status);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);

        var result = await _controller.ApproveAdjustment("adj-1",
            new ApproveAdjustmentRequest { AuthorizedBy = "supervisor" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(ArAdjustmentStatus.Pending)]
    [InlineData(ArAdjustmentStatus.Approved)]
    [InlineData(ArAdjustmentStatus.Reversed)]
    [InlineData(ArAdjustmentStatus.Rejected)]
    public async Task ReverseAdjustment_AllNonPostedStates_ReturnsBadRequest(ArAdjustmentStatus status)
    {
        var adjustment = CreateAdjustment(status: status);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);

        var result = await _controller.ReverseAdjustment("adj-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(ArAdjustmentStatus.Pending)]
    [InlineData(ArAdjustmentStatus.Posted)]
    [InlineData(ArAdjustmentStatus.Reversed)]
    [InlineData(ArAdjustmentStatus.Rejected)]
    public async Task PostAdjustment_AllNonApprovedStates_ReturnsBadRequest(ArAdjustmentStatus status)
    {
        var adjustment = CreateAdjustment(status: status);
        _adjustmentRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);

        var result = await _controller.PostAdjustment("adj-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion
}
