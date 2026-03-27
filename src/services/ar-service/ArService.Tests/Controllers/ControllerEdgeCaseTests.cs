using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ArService.Controllers;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Tests.Controllers;

/// <summary>
/// Edge cases not covered by the primary controller test classes —
/// fallback authorization, null request bodies, boundary state transitions.
/// </summary>
public class ControllerEdgeCaseTests
{
    #region ArAdjustmentsController — ApproveAdjustment AuthorizedBy Fallback

    [Fact]
    public async Task ApproveAdjustment_NullRequest_FallsBackToSystem()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object,
            Mock.Of<ILogger<ArAdjustmentsController>>());

        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Pending);
        adjRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        adjRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var result = await controller.ApproveAdjustment("adj-1", null);

        var ok = result.Result as OkObjectResult;
        var approved = ok!.Value as ArAdjustment;
        approved!.AuthorizedBy.Should().Be("system");
    }

    [Fact]
    public async Task ApproveAdjustment_EmptyAuthorizedBy_FallsBackToSystem()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object,
            Mock.Of<ILogger<ArAdjustmentsController>>());

        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Pending);
        adjRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        adjRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var result = await controller.ApproveAdjustment("adj-1",
            new ApproveAdjustmentRequest { AuthorizedBy = "" });

        var ok = result.Result as OkObjectResult;
        (ok!.Value as ArAdjustment)!.AuthorizedBy.Should().Be("system");
    }

    [Fact]
    public async Task ApproveAdjustment_WhitespaceAuthorizedBy_FallsBackToSystem()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object,
            Mock.Of<ILogger<ArAdjustmentsController>>());

        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Pending);
        adjRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        adjRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var result = await controller.ApproveAdjustment("adj-1",
            new ApproveAdjustmentRequest { AuthorizedBy = "   " });

        var ok = result.Result as OkObjectResult;
        (ok!.Value as ArAdjustment)!.AuthorizedBy.Should().Be("system");
    }

    #endregion

    #region ArAdjustmentsController — CreateAdjustment Amount Boundary

    [Fact]
    public async Task CreateAdjustment_SmallPositiveAmount_Succeeds()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object,
            Mock.Of<ILogger<ArAdjustmentsController>>());

        var adjustment = CreateAdjustment(amount: 0.01m);
        adjRepo.Setup(r => r.CreateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var result = await controller.CreateAdjustment(adjustment);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    #endregion

    #region ArAdjustmentsController — ReverseAdjustment Balance Effects

    [Fact]
    public async Task ReverseAdjustment_DebitAdjustment_SwapsToCredit()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object,
            Mock.Of<ILogger<ArAdjustmentsController>>());

        var adjustment = CreateAdjustment(
            status: ArAdjustmentStatus.Posted,
            amount: 2000m,
            direction: ArAdjustmentDirection.Debit);

        var balance = new ArBalance
        {
            Id = "bal-1",
            TenantId = "tenant-1",
            GlAccountId = "acct-1",
            AccountNumber = "4010",
            Period = new DateTime(2026, 3, 1),
            OpeningBalance = 10000m,
            TotalDebits = 7000m,
            TotalCredits = 2000m,
            ClosingBalance = 15000m
        };

        adjRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        balRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);
        adjRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);
        balRepo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>()))
            .ReturnsAsync((ArBalance b) => b);

        await controller.ReverseAdjustment("adj-1");

        // Debit reversal → credit entry: DebitAmount=0, CreditAmount=2000
        balRepo.Verify(r => r.UpdateAsync(It.Is<ArBalance>(b =>
            b.PostingEntries.Count == 1 &&
            b.PostingEntries[0].DebitAmount == 0m &&
            b.PostingEntries[0].CreditAmount == 2000m &&
            b.PostingEntries[0].SourceReferenceNumber!.StartsWith("REV-")
        )), Times.Once);
    }

    [Fact]
    public async Task ReverseAdjustment_CreditAdjustment_SwapsToDebit()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object,
            Mock.Of<ILogger<ArAdjustmentsController>>());

        var adjustment = CreateAdjustment(
            status: ArAdjustmentStatus.Posted,
            amount: 1500m,
            direction: ArAdjustmentDirection.Credit);

        var balance = new ArBalance
        {
            Id = "bal-1",
            TenantId = "tenant-1",
            GlAccountId = "acct-1",
            AccountNumber = "4010",
            Period = new DateTime(2026, 3, 1),
            OpeningBalance = 10000m,
            TotalDebits = 5000m,
            TotalCredits = 3500m,
            ClosingBalance = 11500m
        };

        adjRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        balRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);
        adjRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);
        balRepo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>()))
            .ReturnsAsync((ArBalance b) => b);

        await controller.ReverseAdjustment("adj-1");

        // Credit reversal → debit entry: DebitAmount=1500, CreditAmount=0
        balRepo.Verify(r => r.UpdateAsync(It.Is<ArBalance>(b =>
            b.PostingEntries.Count == 1 &&
            b.PostingEntries[0].DebitAmount == 1500m &&
            b.PostingEntries[0].CreditAmount == 0m
        )), Times.Once);
    }

    [Fact]
    public async Task ReverseAdjustment_BalanceNotFound_StillReversesAdjustment()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object,
            Mock.Of<ILogger<ArAdjustmentsController>>());

        var adjustment = CreateAdjustment(status: ArAdjustmentStatus.Posted);
        adjRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        balRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync((ArBalance?)null);
        adjRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);

        var result = await controller.ReverseAdjustment("adj-1");

        var ok = result.Result as OkObjectResult;
        (ok!.Value as ArAdjustment)!.Status.Should().Be(ArAdjustmentStatus.Reversed);
        balRepo.Verify(r => r.UpdateAsync(It.IsAny<ArBalance>()), Times.Never);
    }

    [Fact]
    public async Task ReverseAdjustment_RecalculatesClosingBalance()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object,
            Mock.Of<ILogger<ArAdjustmentsController>>());

        var adjustment = CreateAdjustment(
            status: ArAdjustmentStatus.Posted,
            amount: 3000m,
            direction: ArAdjustmentDirection.Debit);

        // Balance after original debit posting: opening=10000, debits=8000, credits=2000 → closing=16000
        var balance = new ArBalance
        {
            Id = "bal-1",
            TenantId = "tenant-1",
            GlAccountId = "acct-1",
            AccountNumber = "4010",
            Period = new DateTime(2026, 3, 1),
            OpeningBalance = 10000m,
            TotalDebits = 8000m,
            TotalCredits = 2000m,
            ClosingBalance = 16000m
        };

        adjRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);
        balRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);
        adjRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>()))
            .ReturnsAsync((ArAdjustment a) => a);
        balRepo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>()))
            .ReturnsAsync((ArBalance b) => b);

        await controller.ReverseAdjustment("adj-1");

        // Reversal of debit=3000 → adds credit=3000
        // New: debits=8000, credits=2000+3000=5000, closing=10000+8000-5000=13000
        balRepo.Verify(r => r.UpdateAsync(It.Is<ArBalance>(b =>
            b.TotalDebits == 8000m &&
            b.TotalCredits == 5000m &&
            b.ClosingBalance == 13000m
        )), Times.Once);
    }

    #endregion

    #region ArBalancesController — ReconcileBalance Fallback

    [Fact]
    public async Task ReconcileBalance_NullRequest_FallsBackToSystem()
    {
        var repo = new Mock<IArBalanceRepository>();
        var controller = new ArBalancesController(repo.Object,
            Mock.Of<ILogger<ArBalancesController>>());

        var balance = new ArBalance
        {
            Id = "bal-1",
            GlAccountId = "acct-1",
            AccountNumber = "4010",
            Period = new DateTime(2026, 3, 1),
            IsReconciled = false
        };
        repo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);
        repo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>()))
            .ReturnsAsync((ArBalance b) => b);

        var result = await controller.ReconcileBalance("bal-1", null);

        var ok = result.Result as OkObjectResult;
        var reconciled = ok!.Value as ArBalance;
        reconciled!.ReconciledBy.Should().Be("system");
        reconciled.ReconciliationNotes.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileBalance_EmptyReconciledBy_FallsBackToSystem()
    {
        var repo = new Mock<IArBalanceRepository>();
        var controller = new ArBalancesController(repo.Object,
            Mock.Of<ILogger<ArBalancesController>>());

        var balance = new ArBalance
        {
            Id = "bal-1",
            GlAccountId = "acct-1",
            AccountNumber = "4010",
            Period = new DateTime(2026, 3, 1),
            IsReconciled = false
        };
        repo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);
        repo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>()))
            .ReturnsAsync((ArBalance b) => b);

        var result = await controller.ReconcileBalance("bal-1",
            new ReconcileRequest { ReconciledBy = "" });

        var ok = result.Result as OkObjectResult;
        (ok!.Value as ArBalance)!.ReconciledBy.Should().Be("system");
    }

    #endregion

    #region CashPostingController — VoidCashPosting PartiallyApplied

    [Fact]
    public async Task VoidCashPosting_PartiallyApplied_Succeeds()
    {
        var repo = new Mock<ICashPostingRepository>();
        var controller = new CashPostingController(repo.Object,
            Mock.Of<ILogger<CashPostingController>>());

        var posting = new CashPosting
        {
            Id = "cp-1",
            PostingNumber = "CP-20260301-TEST",
            Amount = 10000m,
            Status = CashPostingStatus.PartiallyApplied,
            PayerType = PayerType.Sponsor,
            PayerReferenceId = "sponsor-1",
            ReceiptDate = DateTime.Today,
            AppliedAmount = 5000m,
            UnappliedAmount = 5000m
        };
        repo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        repo.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>()))
            .ReturnsAsync((CashPosting p) => p);

        var result = await controller.VoidCashPosting("cp-1");

        var ok = result.Result as OkObjectResult;
        (ok!.Value as CashPosting)!.Status.Should().Be(CashPostingStatus.Voided);
    }

    #endregion

    #region CashPostingController — ApplyCashPosting from PartiallyApplied

    [Fact]
    public async Task ApplyCashPosting_PartiallyApplied_CanReApply()
    {
        var repo = new Mock<ICashPostingRepository>();
        var controller = new CashPostingController(repo.Object,
            Mock.Of<ILogger<CashPostingController>>());

        var posting = new CashPosting
        {
            Id = "cp-1",
            PostingNumber = "CP-20260301-TEST",
            Amount = 10000m,
            Status = CashPostingStatus.PartiallyApplied,
            PayerType = PayerType.Sponsor,
            PayerReferenceId = "sponsor-1",
            ReceiptDate = DateTime.Today,
            Applications = new List<CashApplication>
            {
                new() { AmountApplied = 10000m, GlAccountId = "gl-1", ArBalanceId = "bal-1", Period = DateTime.Today }
            }
        };
        repo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        repo.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>()))
            .ReturnsAsync((CashPosting p) => p);

        var result = await controller.ApplyCashPosting("cp-1");

        var ok = result.Result as OkObjectResult;
        var applied = ok!.Value as CashPosting;
        applied!.Status.Should().Be(CashPostingStatus.Applied);
        applied.AppliedAmount.Should().Be(10000m);
        applied.UnappliedAmount.Should().Be(0m);
    }

    #endregion

    #region ArAdjustmentsController — RejectAdjustment Edge Cases

    [Theory]
    [InlineData(ArAdjustmentStatus.Approved)]
    [InlineData(ArAdjustmentStatus.Posted)]
    [InlineData(ArAdjustmentStatus.Reversed)]
    [InlineData(ArAdjustmentStatus.Rejected)]
    public async Task RejectAdjustment_AllNonPendingStates_ReturnsBadRequest(ArAdjustmentStatus status)
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object,
            Mock.Of<ILogger<ArAdjustmentsController>>());

        var adjustment = CreateAdjustment(status: status);
        adjRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(adjustment);

        var result = await controller.RejectAdjustment("adj-1",
            new RejectAdjustmentRequest { Reason = "test" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region ArBatchRulesController — TestBatchRule SplitByPlan

    [Fact]
    public async Task TestBatchRule_SplitByPlan_ReturnsBalancedAmount()
    {
        var repo = new Mock<IArBatchRuleRepository>();
        var controller = new ArBatchRulesController(repo.Object,
            Mock.Of<ILogger<ArBatchRulesController>>());

        var rule = new ArBatchRule
        {
            Id = "rule-1",
            RuleCode = "PREM-SPLIT",
            RuleName = "Premium Split by Plan",
            Trigger = BatchRuleTrigger.PremiumBillingRunComplete,
            SplitBehavior = BatchRuleSplitBehavior.SplitByPlan,
            DebitAccountId = "acct-dr",
            CreditAccountId = "acct-cr"
        };
        repo.Setup(r => r.GetByIdAsync("rule-1")).ReturnsAsync(rule);

        var request = new TestBatchRuleRequest { SampleAmount = 15000m };
        var result = await controller.TestBatchRule("rule-1", request);

        var ok = result.Result as OkObjectResult;
        var testResult = ok!.Value as TestBatchRuleResult;
        testResult!.ProjectedDebitAmount.Should().Be(15000m);
        testResult.ProjectedCreditAmount.Should().Be(15000m);
        testResult.SplitBehavior.Should().Be(BatchRuleSplitBehavior.SplitByPlan);
    }

    #endregion

    #region GlAccountsController — UpdateAccount Preserves Id

    [Fact]
    public async Task UpdateAccount_SetsIdFromRoute()
    {
        var repo = new Mock<IGlAccountRepository>();
        var controller = new GlAccountsController(repo.Object,
            Mock.Of<ILogger<GlAccountsController>>());

        var existing = new GlAccount
        {
            Id = "acct-1",
            TenantId = "tenant-1",
            AccountNumber = "4010",
            AccountName = "Original",
            CreatedAt = new DateTime(2025, 1, 1),
            CreatedBy = "admin"
        };
        repo.Setup(r => r.GetByIdAsync("acct-1")).ReturnsAsync(existing);
        repo.Setup(r => r.UpdateAsync(It.IsAny<GlAccount>()))
            .ReturnsAsync((GlAccount a) => a);

        var incoming = new GlAccount
        {
            Id = "different-id",
            AccountNumber = "4010",
            AccountName = "Updated"
        };
        var result = await controller.UpdateAccount("acct-1", incoming);

        var ok = result.Result as OkObjectResult;
        (ok!.Value as GlAccount)!.Id.Should().Be("acct-1");
    }

    #endregion

    #region ArBalancesController — GetAgingSummary TotalOutstanding

    [Fact]
    public async Task GetAgingSummary_TotalOutstandingMatchesTotal()
    {
        var repo = new Mock<IArBalanceRepository>();
        var controller = new ArBalancesController(repo.Object,
            Mock.Of<ILogger<ArBalancesController>>());

        var balances = new List<ArBalance>
        {
            new() { Current = 100m, Days31To60 = 200m, Days61To90 = 300m, Days91To120 = 400m, Over120Days = 500m }
        };
        repo.Setup(r => r.SearchAsync(null, null, null, 1, int.MaxValue)).ReturnsAsync(balances);

        var result = await controller.GetAgingSummary();

        var ok = result.Result as OkObjectResult;
        var summary = ok!.Value as AgingSummary;
        summary!.TotalOutstanding.Should().Be(summary.Total);
        summary.Total.Should().Be(1500m);
    }

    #endregion

    #region Factory Methods

    private static ArAdjustment CreateAdjustment(
        string id = "adj-1",
        ArAdjustmentStatus status = ArAdjustmentStatus.Pending,
        decimal amount = 5000m,
        ArAdjustmentDirection direction = ArAdjustmentDirection.Debit) => new()
    {
        Id = id,
        TenantId = "tenant-1",
        AdjustmentNumber = "ADJ-20260301-TEST0001",
        AdjustmentType = ArAdjustmentType.ManualCorrection,
        GlAccountId = "acct-1",
        ArBalanceId = "bal-1",
        Period = new DateTime(2026, 3, 1),
        Amount = amount,
        Direction = direction,
        ReasonCode = "MC-001",
        Status = status,
        CreatedBy = "finance-user"
    };

    #endregion
}
