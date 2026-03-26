using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ArService.Controllers;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Tests.Controllers;

public class ArBalancesControllerTests
{
    private readonly Mock<IArBalanceRepository> _balanceRepo;
    private readonly ArBalancesController _controller;

    public ArBalancesControllerTests()
    {
        _balanceRepo = new Mock<IArBalanceRepository>();
        var logger = new Mock<ILogger<ArBalancesController>>();
        _controller = new ArBalancesController(_balanceRepo.Object, logger.Object);
    }

    private static ArBalance CreateBalance(
        string id = "bal-1",
        string glAccountId = "acct-1",
        string accountNumber = "4010",
        decimal openingBalance = 10000m,
        decimal totalDebits = 5000m,
        decimal totalCredits = 2000m,
        decimal current = 3000m,
        decimal days31To60 = 2000m,
        decimal days61To90 = 1000m,
        decimal days91To120 = 500m,
        decimal over120Days = 200m,
        bool isReconciled = false) => new()
    {
        Id = id,
        TenantId = "tenant-1",
        GlAccountId = glAccountId,
        AccountNumber = accountNumber,
        Period = new DateTime(2026, 3, 1),
        OpeningBalance = openingBalance,
        TotalDebits = totalDebits,
        TotalCredits = totalCredits,
        ClosingBalance = openingBalance + totalDebits - totalCredits,
        Current = current,
        Days31To60 = days31To60,
        Days61To90 = days61To90,
        Days91To120 = days91To120,
        Over120Days = over120Days,
        IsReconciled = isReconciled
    };

    #region SearchBalances

    [Fact]
    public async Task SearchBalances_NoFilters_ReturnsAll()
    {
        var balances = new List<ArBalance> { CreateBalance(), CreateBalance("bal-2", "acct-2") };
        _balanceRepo.Setup(r => r.SearchAsync(null, null, null, 1, 50)).ReturnsAsync(balances);

        var result = await _controller.SearchBalances();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<ArBalance>)!.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchBalances_FilterByAccountId_PassesThrough()
    {
        _balanceRepo.Setup(r => r.SearchAsync("acct-1", null, null, 1, 50))
            .ReturnsAsync(new List<ArBalance> { CreateBalance() });

        var result = await _controller.SearchBalances(accountId: "acct-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        _balanceRepo.Verify(r => r.SearchAsync("acct-1", null, null, 1, 50), Times.Once);
    }

    [Fact]
    public async Task SearchBalances_FilterByPeriod_PassesThrough()
    {
        var period = new DateTime(2026, 3, 1);
        _balanceRepo.Setup(r => r.SearchAsync(null, period, null, 1, 50))
            .ReturnsAsync(new List<ArBalance>());

        await _controller.SearchBalances(period: period);

        _balanceRepo.Verify(r => r.SearchAsync(null, period, null, 1, 50), Times.Once);
    }

    [Fact]
    public async Task SearchBalances_FilterByIsReconciled_PassesThrough()
    {
        _balanceRepo.Setup(r => r.SearchAsync(null, null, false, 1, 50))
            .ReturnsAsync(new List<ArBalance> { CreateBalance() });

        await _controller.SearchBalances(isReconciled: false);

        _balanceRepo.Verify(r => r.SearchAsync(null, null, false, 1, 50), Times.Once);
    }

    #endregion

    #region GetBalanceById

    [Fact]
    public async Task GetBalanceById_Found_ReturnsOk()
    {
        _balanceRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(CreateBalance());

        var result = await _controller.GetBalanceById("bal-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as ArBalance)!.GlAccountId.Should().Be("acct-1");
    }

    [Fact]
    public async Task GetBalanceById_NotFound_Returns404()
    {
        _balanceRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ArBalance?)null);

        var result = await _controller.GetBalanceById("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region GetBalancesByAccountId

    [Fact]
    public async Task GetBalancesByAccountId_ReturnsAllForAccount()
    {
        var balances = new List<ArBalance>
        {
            CreateBalance("bal-1", "acct-1"),
            CreateBalance("bal-2", "acct-1")
        };
        _balanceRepo.Setup(r => r.GetByAccountIdAsync("acct-1")).ReturnsAsync(balances);

        var result = await _controller.GetBalancesByAccountId("acct-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<ArBalance>)!.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetBalancesByAccountId_NoBalances_ReturnsEmptyList()
    {
        _balanceRepo.Setup(r => r.GetByAccountIdAsync("acct-orphan"))
            .ReturnsAsync(new List<ArBalance>());

        var result = await _controller.GetBalancesByAccountId("acct-orphan");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<ArBalance>)!.Should().BeEmpty();
    }

    #endregion

    #region ReconcileBalance

    [Fact]
    public async Task ReconcileBalance_SetsIsReconciledAndReconciledByAndReconciledAt()
    {
        var balance = CreateBalance(isReconciled: false);
        _balanceRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);
        _balanceRepo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>()))
            .ReturnsAsync((ArBalance b) => b);

        var beforeReconcile = DateTime.UtcNow;
        var request = new ReconcileRequest { ReconciledBy = "finance-user", Notes = "Month-end close" };
        var result = await _controller.ReconcileBalance("bal-1", request);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var reconciled = ok!.Value as ArBalance;
        reconciled!.IsReconciled.Should().BeTrue();
        reconciled.ReconciledBy.Should().Be("finance-user");
        reconciled.ReconciledAt.Should().NotBeNull();
        reconciled.ReconciledAt!.Value.Should().BeOnOrAfter(beforeReconcile);
        reconciled.ReconciliationNotes.Should().Be("Month-end close");
    }

    [Fact]
    public async Task ReconcileBalance_NotFound_Returns404()
    {
        _balanceRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ArBalance?)null);

        var result = await _controller.ReconcileBalance("missing",
            new ReconcileRequest { ReconciledBy = "user" });

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ReconcileBalance_AlreadyReconciled_ReturnsBadRequest()
    {
        // Guard: already-reconciled balances cannot be re-reconciled without explicit un-reconcile
        var balance = CreateBalance(isReconciled: true);
        balance.ReconciledBy = "old-user";
        balance.ReconciledAt = new DateTime(2026, 2, 28);
        _balanceRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);

        var request = new ReconcileRequest { ReconciledBy = "new-user", Notes = "Re-reconcile" };
        var result = await _controller.ReconcileBalance("bal-1", request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region AgingSummary

    [Fact]
    public async Task GetAgingSummary_AggregatesAcrossMultipleBalances()
    {
        // CRITICAL: Aging buckets must sum correctly across all balances.
        // Real-world scenario: 3 accounts with different aging distributions.
        var balances = new List<ArBalance>
        {
            CreateBalance("bal-1", current: 10000.00m, days31To60: 5000.50m, days61To90: 2000.25m,
                          days91To120: 1000.10m, over120Days: 500.05m),
            CreateBalance("bal-2", current: 8000.00m, days31To60: 3000.00m, days61To90: 1500.75m,
                          days91To120: 750.50m, over120Days: 250.25m),
            CreateBalance("bal-3", current: 2000.00m, days31To60: 1000.00m, days61To90: 500.00m,
                          days91To120: 250.00m, over120Days: 100.00m)
        };
        _balanceRepo.Setup(r => r.SearchAsync(null, null, null, 1, int.MaxValue)).ReturnsAsync(balances);

        var result = await _controller.GetAgingSummary();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var summary = ok!.Value as AgingSummary;
        summary.Should().NotBeNull();

        // Verify each aging bucket sums correctly
        summary!.Current.Should().Be(10000.00m + 8000.00m + 2000.00m);  // 20000.00
        summary.Days31To60.Should().Be(5000.50m + 3000.00m + 1000.00m); // 9000.50
        summary.Days61To90.Should().Be(2000.25m + 1500.75m + 500.00m);  // 4001.00
        summary.Days91To120.Should().Be(1000.10m + 750.50m + 250.00m);  // 2000.60
        summary.Over120Days.Should().Be(500.05m + 250.25m + 100.00m);   // 850.30

        // Verify total = sum of all buckets
        var expectedTotal = 20000.00m + 9000.50m + 4001.00m + 2000.60m + 850.30m; // 35852.40
        summary.Total.Should().Be(expectedTotal);
    }

    [Fact]
    public async Task GetAgingSummary_NoBalances_ReturnsZeroAcrossAllBuckets()
    {
        _balanceRepo.Setup(r => r.SearchAsync(null, null, null, 1, int.MaxValue))
            .ReturnsAsync(new List<ArBalance>());

        var result = await _controller.GetAgingSummary();

        var ok = result.Result as OkObjectResult;
        var summary = ok!.Value as AgingSummary;
        summary!.Current.Should().Be(0m);
        summary.Days31To60.Should().Be(0m);
        summary.Days61To90.Should().Be(0m);
        summary.Days91To120.Should().Be(0m);
        summary.Over120Days.Should().Be(0m);
        summary.Total.Should().Be(0m);
    }

    [Fact]
    public async Task GetAgingSummary_SingleBalance_TotalEqualsSumOfBuckets()
    {
        var balance = CreateBalance(current: 5000m, days31To60: 3000m, days61To90: 2000m,
                                     days91To120: 1000m, over120Days: 500m);
        _balanceRepo.Setup(r => r.SearchAsync(null, null, null, 1, int.MaxValue))
            .ReturnsAsync(new List<ArBalance> { balance });

        var result = await _controller.GetAgingSummary();

        var ok = result.Result as OkObjectResult;
        var summary = ok!.Value as AgingSummary;
        summary!.Total.Should().Be(5000m + 3000m + 2000m + 1000m + 500m); // 11500
        summary.Total.Should().Be(summary.Current + summary.Days31To60 + summary.Days61To90
            + summary.Days91To120 + summary.Over120Days);
    }

    [Fact]
    public async Task GetAgingSummary_DecimalPrecision_HandlesSmallAmounts()
    {
        // Financial systems must handle penny-level precision without rounding errors
        var balances = new List<ArBalance>
        {
            CreateBalance("bal-1", current: 0.01m, days31To60: 0.02m, days61To90: 0.03m,
                          days91To120: 0.04m, over120Days: 0.05m),
            CreateBalance("bal-2", current: 0.99m, days31To60: 0.98m, days61To90: 0.97m,
                          days91To120: 0.96m, over120Days: 0.95m)
        };
        _balanceRepo.Setup(r => r.SearchAsync(null, null, null, 1, int.MaxValue)).ReturnsAsync(balances);

        var result = await _controller.GetAgingSummary();

        var ok = result.Result as OkObjectResult;
        var summary = ok!.Value as AgingSummary;
        summary!.Current.Should().Be(1.00m);
        summary.Days31To60.Should().Be(1.00m);
        summary.Days61To90.Should().Be(1.00m);
        summary.Days91To120.Should().Be(1.00m);
        summary.Over120Days.Should().Be(1.00m);
        summary.Total.Should().Be(5.00m);
    }

    #endregion
}
