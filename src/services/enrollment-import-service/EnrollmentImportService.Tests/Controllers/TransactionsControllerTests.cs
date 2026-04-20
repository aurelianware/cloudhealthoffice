using EnrollmentImportService.Controllers;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace EnrollmentImportService.Tests.Controllers;

public class TransactionsControllerTests
{
    private static (TransactionsController ctl, Mock<IEnrollmentTransactionRepository> repo) Build()
    {
        var repo = new Mock<IEnrollmentTransactionRepository>();
        var ctl = new TransactionsController(repo.Object);
        return (ctl, repo);
    }

    [Fact]
    public async Task ListTransactions_ReturnsRepoResult()
    {
        var (ctl, repo) = Build();
        repo.Setup(r => r.ListByMemberAsync("t1", "M-001", 100))
            .ReturnsAsync(new List<EnrollmentTransaction>
            {
                new() { TenantId = "t1", MemberId = "M-001", BatchId = "B1", TransactionId = "T1" }
            });

        var resp = await ctl.ListTransactions("t1", "M-001", 100);
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var list = (IReadOnlyList<EnrollmentTransaction>)ok.Value!;
        list.Should().ContainSingle();
    }

    [Fact]
    public async Task ListTransactions_MissingTenant_ReturnsBadRequest()
    {
        var (ctl, _) = Build();
        (await ctl.ListTransactions("", "M-001")).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ListTransactions_MissingMemberId_ReturnsBadRequest()
    {
        var (ctl, _) = Build();
        (await ctl.ListTransactions("t1", "")).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ListTransactions_LimitOutOfRange_ClampsTo100()
    {
        var (ctl, repo) = Build();
        repo.Setup(r => r.ListByMemberAsync("t1", "M-001", 100))
            .ReturnsAsync(new List<EnrollmentTransaction>());

        await ctl.ListTransactions("t1", "M-001", 99999);
        repo.Verify(r => r.ListByMemberAsync("t1", "M-001", 100), Times.Once);

        await ctl.ListTransactions("t1", "M-001", 0);
        repo.Verify(r => r.ListByMemberAsync("t1", "M-001", 100), Times.Exactly(2));
    }
}
