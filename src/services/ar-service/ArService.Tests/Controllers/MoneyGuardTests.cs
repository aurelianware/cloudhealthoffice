using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ArService.Controllers;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Tests.Controllers;

/// <summary>
/// Critical money-safety tests — guards against financial calculation errors,
/// illegal state transitions, and data integrity violations that would cause
/// real-dollar discrepancies in AR, cash posting, and adjustment workflows.
/// </summary>
public class MoneyGuardTests
{
    #region Cash Posting — Over-application & Amount Guards

    [Fact]
    public async Task ApplyCashPosting_OverApplication_ReturnsBadRequest()
    {
        // If applications sum to MORE than the receipt amount, cash reconciliation breaks
        var repo = new Mock<ICashPostingRepository>();
        var controller = new CashPostingController(repo.Object, Mock.Of<ILogger<CashPostingController>>());

        var posting = CreateCashPosting(amount: 1000.00m, status: CashPostingStatus.Pending);
        posting.Applications = new()
        {
            new() { AmountApplied = 600.00m, GlAccountId = "gl-1", ArBalanceId = "bal-1", Period = DateTime.Today },
            new() { AmountApplied = 500.00m, GlAccountId = "gl-1", ArBalanceId = "bal-2", Period = DateTime.Today }
            // Total: 1100.00 > 1000.00
        };
        repo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);

        var result = await controller.ApplyCashPosting("cp-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ApplyCashPosting_OverApplicationByOnePenny_ReturnsBadRequest()
    {
        // Even one penny over is an error — no tolerance
        var repo = new Mock<ICashPostingRepository>();
        var controller = new CashPostingController(repo.Object, Mock.Of<ILogger<CashPostingController>>());

        var posting = CreateCashPosting(amount: 100.00m, status: CashPostingStatus.Pending);
        posting.Applications = new()
        {
            new() { AmountApplied = 100.01m, GlAccountId = "gl-1", ArBalanceId = "bal-1", Period = DateTime.Today }
        };
        repo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);

        var result = await controller.ApplyCashPosting("cp-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ApplyCashPosting_ExactAmount_Succeeds()
    {
        var repo = new Mock<ICashPostingRepository>();
        var controller = new CashPostingController(repo.Object, Mock.Of<ILogger<CashPostingController>>());

        var posting = CreateCashPosting(amount: 5000.00m, status: CashPostingStatus.Pending);
        posting.Applications = new()
        {
            new() { AmountApplied = 3000.00m, GlAccountId = "gl-1", ArBalanceId = "bal-1", Period = DateTime.Today },
            new() { AmountApplied = 2000.00m, GlAccountId = "gl-1", ArBalanceId = "bal-2", Period = DateTime.Today }
        };
        repo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        repo.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>())).ReturnsAsync((CashPosting p) => p);

        var result = await controller.ApplyCashPosting("cp-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var updated = ok!.Value as CashPosting;
        updated!.AppliedAmount.Should().Be(5000.00m);
        updated.UnappliedAmount.Should().Be(0m);
        updated.Status.Should().Be(CashPostingStatus.Applied);
    }

    [Fact]
    public async Task ApplyCashPosting_PartialAmount_SetsPartiallyApplied()
    {
        var repo = new Mock<ICashPostingRepository>();
        var controller = new CashPostingController(repo.Object, Mock.Of<ILogger<CashPostingController>>());

        var posting = CreateCashPosting(amount: 10000.00m, status: CashPostingStatus.Pending);
        posting.Applications = new()
        {
            new() { AmountApplied = 7500.00m, GlAccountId = "gl-1", ArBalanceId = "bal-1", Period = DateTime.Today }
        };
        repo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        repo.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>())).ReturnsAsync((CashPosting p) => p);

        var result = await controller.ApplyCashPosting("cp-1");

        var ok = result.Result as OkObjectResult;
        var updated = (ok!.Value as CashPosting)!;
        updated.Status.Should().Be(CashPostingStatus.PartiallyApplied);
        updated.UnappliedAmount.Should().Be(2500.00m);
    }

    [Fact]
    public async Task ApplyCashPosting_NegativeApplicationAmount_ReturnsBadRequest()
    {
        var repo = new Mock<ICashPostingRepository>();
        var controller = new CashPostingController(repo.Object, Mock.Of<ILogger<CashPostingController>>());

        var posting = CreateCashPosting(amount: 1000.00m, status: CashPostingStatus.Pending);
        posting.Applications = new()
        {
            new() { AmountApplied = -500.00m, GlAccountId = "gl-1", ArBalanceId = "bal-1", Period = DateTime.Today }
        };
        repo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);

        var result = await controller.ApplyCashPosting("cp-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ApplyCashPosting_AlreadyApplied_ReturnsBadRequest()
    {
        var repo = new Mock<ICashPostingRepository>();
        var controller = new CashPostingController(repo.Object, Mock.Of<ILogger<CashPostingController>>());

        var posting = CreateCashPosting(amount: 1000.00m, status: CashPostingStatus.Applied);
        repo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);

        var result = await controller.ApplyCashPosting("cp-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task VoidCashPosting_AlreadyApplied_ReturnsBadRequest()
    {
        // Can't void cash that's already been applied to balances
        var repo = new Mock<ICashPostingRepository>();
        var controller = new CashPostingController(repo.Object, Mock.Of<ILogger<CashPostingController>>());

        var posting = CreateCashPosting(amount: 1000.00m, status: CashPostingStatus.Applied);
        repo.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);

        var result = await controller.VoidCashPosting("cp-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region AR Adjustments — Negative Amount & State Guards

    [Fact]
    public async Task CreateAdjustment_NegativeAmount_ReturnsBadRequest()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object, Mock.Of<ILogger<ArAdjustmentsController>>());

        var adjustment = CreateAdjustment(amount: -500.00m);

        var result = await controller.CreateAdjustment(adjustment);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateAdjustment_ZeroAmount_ReturnsBadRequest()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object, Mock.Of<ILogger<ArAdjustmentsController>>());

        var adjustment = CreateAdjustment(amount: 0m);

        var result = await controller.CreateAdjustment(adjustment);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateAdjustment_PositiveAmount_Succeeds()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object, Mock.Of<ILogger<ArAdjustmentsController>>());

        var adjustment = CreateAdjustment(amount: 1500.00m);
        adjRepo.Setup(r => r.CreateAsync(It.IsAny<ArAdjustment>())).ReturnsAsync((ArAdjustment a) => a);

        var result = await controller.CreateAdjustment(adjustment);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    #endregion

    #region AR Balance — Reconciliation Guards

    [Fact]
    public async Task ReconcileBalance_AlreadyReconciled_ReturnsBadRequest()
    {
        var repo = new Mock<IArBalanceRepository>();
        var controller = new ArBalancesController(repo.Object, Mock.Of<ILogger<ArBalancesController>>());

        var balance = CreateBalance(isReconciled: true);
        repo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);

        var result = await controller.ReconcileBalance("bal-1", new ReconcileRequest { ReconciledBy = "auditor" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ReconcileBalance_NotYetReconciled_Succeeds()
    {
        var repo = new Mock<IArBalanceRepository>();
        var controller = new ArBalancesController(repo.Object, Mock.Of<ILogger<ArBalancesController>>());

        var balance = CreateBalance(isReconciled: false);
        repo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(balance);
        repo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>())).ReturnsAsync((ArBalance b) => b);

        var result = await controller.ReconcileBalance("bal-1", new ReconcileRequest { ReconciledBy = "auditor" });

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var updated = (ok!.Value as ArBalance)!;
        updated.IsReconciled.Should().BeTrue();
        updated.ReconciledBy.Should().Be("auditor");
    }

    #endregion

    #region Provider Contracts — Date Validation

    [Fact]
    public async Task CreateContract_TerminationBeforeEffective_ReturnsBadRequest()
    {
        var repo = new Mock<ProviderContractsService.Repositories.IProviderContractRepository>();
        var controller = new ProviderContractsService.Controllers.ProviderContractsController(
            repo.Object, Mock.Of<ILogger<ProviderContractsService.Controllers.ProviderContractsController>>());

        var contract = new ProviderContractsService.Models.ProviderContract
        {
            ProviderNPI = "1234567890",
            EffectiveDate = new DateTime(2026, 6, 1),
            TerminationDate = new DateTime(2026, 1, 1) // Before effective!
        };

        var result = await controller.CreateContract(contract);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateContract_TerminationAfterEffective_Succeeds()
    {
        var repo = new Mock<ProviderContractsService.Repositories.IProviderContractRepository>();
        var controller = new ProviderContractsService.Controllers.ProviderContractsController(
            repo.Object, Mock.Of<ILogger<ProviderContractsService.Controllers.ProviderContractsController>>());

        var contract = new ProviderContractsService.Models.ProviderContract
        {
            ProviderNPI = "1234567890",
            EffectiveDate = new DateTime(2026, 1, 1),
            TerminationDate = new DateTime(2026, 12, 31)
        };
        repo.Setup(r => r.CreateAsync(It.IsAny<ProviderContractsService.Models.ProviderContract>()))
            .ReturnsAsync((ProviderContractsService.Models.ProviderContract c) => c);

        var result = await controller.CreateContract(contract);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task CreateContract_NoTerminationDate_Succeeds()
    {
        // Open-ended contracts (null termination) are valid
        var repo = new Mock<ProviderContractsService.Repositories.IProviderContractRepository>();
        var controller = new ProviderContractsService.Controllers.ProviderContractsController(
            repo.Object, Mock.Of<ILogger<ProviderContractsService.Controllers.ProviderContractsController>>());

        var contract = new ProviderContractsService.Models.ProviderContract
        {
            ProviderNPI = "1234567890",
            EffectiveDate = new DateTime(2026, 1, 1),
            TerminationDate = null
        };
        repo.Setup(r => r.CreateAsync(It.IsAny<ProviderContractsService.Models.ProviderContract>()))
            .ReturnsAsync((ProviderContractsService.Models.ProviderContract c) => c);

        var result = await controller.CreateContract(contract);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    #endregion

    #region Factory Methods

    private static CashPosting CreateCashPosting(
        string id = "cp-1",
        decimal amount = 1000.00m,
        CashPostingStatus status = CashPostingStatus.Pending) => new()
    {
        Id = id,
        PostingNumber = "CP-20260325-TEST",
        Amount = amount,
        Status = status,
        PayerType = PayerType.Sponsor,
        PayerReferenceId = "sponsor-1",
        ReceiptDate = DateTime.Today,
        Applications = new()
    };

    private static ArAdjustment CreateAdjustment(
        string id = "adj-1",
        decimal amount = 1000.00m,
        ArAdjustmentDirection direction = ArAdjustmentDirection.Debit) => new()
    {
        Id = id,
        Amount = amount,
        Direction = direction,
        AdjustmentType = ArAdjustmentType.ManualCorrection,
        GlAccountId = "gl-1",
        ArBalanceId = "bal-1",
        Period = new DateTime(2026, 3, 1),
        ReasonCode = "TEST"
    };

    private static ArBalance CreateBalance(
        string id = "bal-1",
        bool isReconciled = false,
        decimal openingBalance = 10000.00m) => new()
    {
        Id = id,
        GlAccountId = "gl-1",
        AccountNumber = "4010",
        Period = new DateTime(2026, 3, 1),
        OpeningBalance = openingBalance,
        IsReconciled = isReconciled
    };

    #endregion
}
