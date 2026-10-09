using ArService.Controllers;
using ArService.Models;
using ArService.Repositories;
using ArService.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ArService.Tests.Security;

/// <summary>
/// Every AR write records the authenticated user. Actor names in the request
/// body are ignored.
/// </summary>
public class ActorFromTokenTests
{
    private const string Attacker = "someone-else";
    private readonly TestActor _actor = new("finance-user-7");

    [Fact]
    public async Task ApproveAdjustment_IgnoresBodyAuthorizedBy()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        adjRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(new ArAdjustment
        {
            Id = "adj-1", Status = ArAdjustmentStatus.Pending, Amount = 10m
        });
        adjRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>())).ReturnsAsync((ArAdjustment a) => a);
        var controller = new ArAdjustmentsController(adjRepo.Object, Mock.Of<IArBalanceRepository>(), _actor,
            Mock.Of<ILogger<ArAdjustmentsController>>());

        var result = await controller.ApproveAdjustment("adj-1", new ApproveAdjustmentRequest { AuthorizedBy = Attacker });

        var approved = (ArAdjustment)((OkObjectResult)result.Result!).Value!;
        approved.AuthorizedBy.Should().Be("finance-user-7");
    }

    [Fact]
    public async Task CreateAdjustment_IgnoresBodyCreatedByAndPreApproval()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        adjRepo.Setup(r => r.CreateAsync(It.IsAny<ArAdjustment>())).ReturnsAsync((ArAdjustment a) => a);
        var controller = new ArAdjustmentsController(adjRepo.Object, Mock.Of<IArBalanceRepository>(), _actor,
            Mock.Of<ILogger<ArAdjustmentsController>>());

        var result = await controller.CreateAdjustment(new ArAdjustment
        {
            Amount = 10m, CreatedBy = Attacker, AuthorizedBy = Attacker, AuthorizedAt = DateTime.UtcNow
        });

        var created = (ArAdjustment)((CreatedAtActionResult)result.Result!).Value!;
        created.CreatedBy.Should().Be("finance-user-7");
        created.AuthorizedBy.Should().BeNull();
        created.AuthorizedAt.Should().BeNull();
    }

    [Fact]
    public async Task PostAdjustment_RecordsPosterFromToken()
    {
        var adjRepo = new Mock<IArAdjustmentRepository>();
        var balRepo = new Mock<IArBalanceRepository>();
        adjRepo.Setup(r => r.GetByIdAsync("adj-1")).ReturnsAsync(new ArAdjustment
        {
            Id = "adj-1", ArBalanceId = "bal-1", Status = ArAdjustmentStatus.Approved,
            Amount = 10m, AuthorizedBy = "approver"
        });
        adjRepo.Setup(r => r.UpdateAsync(It.IsAny<ArAdjustment>())).ReturnsAsync((ArAdjustment a) => a);
        balRepo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(new ArBalance { Id = "bal-1" });
        balRepo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>())).ReturnsAsync((ArBalance b) => b);
        var controller = new ArAdjustmentsController(adjRepo.Object, balRepo.Object, _actor,
            Mock.Of<ILogger<ArAdjustmentsController>>());

        await controller.PostAdjustment("adj-1");

        balRepo.Verify(r => r.UpdateAsync(It.Is<ArBalance>(b => b.PostingEntries[0].PostedBy == "finance-user-7")));
    }

    [Fact]
    public async Task ReconcileBalance_IgnoresBodyReconciledBy()
    {
        var repo = new Mock<IArBalanceRepository>();
        repo.Setup(r => r.GetByIdAsync("bal-1")).ReturnsAsync(new ArBalance { Id = "bal-1" });
        repo.Setup(r => r.UpdateAsync(It.IsAny<ArBalance>())).ReturnsAsync((ArBalance b) => b);
        var controller = new ArBalancesController(repo.Object, _actor, Mock.Of<ILogger<ArBalancesController>>());

        var result = await controller.ReconcileBalance("bal-1", new ReconcileRequest { ReconciledBy = Attacker });

        var balance = (ArBalance)((OkObjectResult)result.Result!).Value!;
        balance.ReconciledBy.Should().Be("finance-user-7");
    }

    [Fact]
    public async Task CreateCashPosting_IgnoresBodyCreatedBy()
    {
        var repo = new Mock<ICashPostingRepository>();
        repo.Setup(r => r.CreateAsync(It.IsAny<CashPosting>())).ReturnsAsync((CashPosting p) => p);
        var controller = new CashPostingController(repo.Object, new FakeArBalanceRepository(), _actor, Mock.Of<ILogger<CashPostingController>>());

        var result = await controller.CreateCashPosting(new CashPosting { CreatedBy = Attacker });

        var created = (CashPosting)((CreatedAtActionResult)result.Result!).Value!;
        created.CreatedBy.Should().Be("finance-user-7");
    }

    [Fact]
    public async Task GlAccount_CreateAndUpdate_RecordActorFromToken()
    {
        var repo = new Mock<IGlAccountRepository>();
        repo.Setup(r => r.CreateAsync(It.IsAny<GlAccount>())).ReturnsAsync((GlAccount a) => a);
        repo.Setup(r => r.UpdateAsync(It.IsAny<GlAccount>())).ReturnsAsync((GlAccount a) => a);
        repo.Setup(r => r.GetByIdAsync("acct-1")).ReturnsAsync(new GlAccount { Id = "acct-1", CreatedBy = "original" });
        var controller = new GlAccountsController(repo.Object, _actor, Mock.Of<ILogger<GlAccountsController>>());

        var created = await controller.CreateAccount(new GlAccount { CreatedBy = Attacker, LastUpdatedBy = Attacker });
        var updated = await controller.UpdateAccount("acct-1", new GlAccount { CreatedBy = Attacker, LastUpdatedBy = Attacker });

        var c = (GlAccount)((CreatedAtActionResult)created.Result!).Value!;
        c.CreatedBy.Should().Be("finance-user-7");
        c.LastUpdatedBy.Should().BeNull();
        var u = (GlAccount)((OkObjectResult)updated.Result!).Value!;
        u.CreatedBy.Should().Be("original");
        u.LastUpdatedBy.Should().Be("finance-user-7");
    }

    [Fact]
    public async Task CreateBatchRule_IgnoresBodyCreatedBy()
    {
        var repo = new Mock<IArBatchRuleRepository>();
        repo.Setup(r => r.CreateAsync(It.IsAny<ArBatchRule>())).ReturnsAsync((ArBatchRule r) => r);
        var controller = new ArBatchRulesController(repo.Object, _actor, Mock.Of<ILogger<ArBatchRulesController>>());

        var result = await controller.CreateBatchRule(new ArBatchRule { CreatedBy = Attacker });

        var created = (ArBatchRule)((CreatedAtActionResult)result.Result!).Value!;
        created.CreatedBy.Should().Be("finance-user-7");
    }
}
