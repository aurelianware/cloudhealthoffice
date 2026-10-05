using EnrollmentImportService.Controllers;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using Microsoft.AspNetCore.Mvc;
using Moq;

using EnrollmentImportService.Tests.Support;

namespace EnrollmentImportService.Tests.Controllers;

public class TransactionsControllerTests
{
    private static (TransactionsController ctl, Mock<IEnrollmentTransactionRepository> repo) Build()
    {
        var repo = new Mock<IEnrollmentTransactionRepository>();
        var ctl = new TransactionsController(repo.Object, new TestActor(tenantId: "t1"));
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

        var resp = await ctl.ListTransactions("M-001", 100);
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var list = (IReadOnlyList<EnrollmentTransaction>)ok.Value!;
        list.Should().ContainSingle();
    }

    [Fact]
    public async Task ListTransactions_MissingMemberId_ReturnsBadRequest()
    {
        var (ctl, _) = Build();
        (await ctl.ListTransactions("")).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ListTransactions_LimitOutOfRange_ClampsTo100()
    {
        var (ctl, repo) = Build();
        repo.Setup(r => r.ListByMemberAsync("t1", "M-001", 100))
            .ReturnsAsync(new List<EnrollmentTransaction>());

        await ctl.ListTransactions("M-001", 99999);
        repo.Verify(r => r.ListByMemberAsync("t1", "M-001", 100), Times.Once);

        await ctl.ListTransactions("M-001", 0);
        repo.Verify(r => r.ListByMemberAsync("t1", "M-001", 100), Times.Exactly(2));
    }

    [Fact]
    public async Task ListRecentTransactions_ReturnsRepoResult()
    {
        var (ctl, repo) = Build();
        repo.Setup(r => r.ListRecentAsync("t1", 100))
            .ReturnsAsync(new List<EnrollmentTransaction>
            {
                new() { TenantId = "t1", MemberId = "M-001", BatchId = "B1", TransactionId = "T1" },
                new() { TenantId = "t1", MemberId = "M-002", BatchId = "B1", TransactionId = "T2" }
            });

        var resp = await ctl.ListRecentTransactions(100);
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var list = (IReadOnlyList<EnrollmentTransaction>)ok.Value!;
        list.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListRecentTransactions_LimitOutOfRange_ClampsTo100()
    {
        var (ctl, repo) = Build();
        repo.Setup(r => r.ListRecentAsync("t1", 100))
            .ReturnsAsync(new List<EnrollmentTransaction>());

        await ctl.ListRecentTransactions(99999);
        repo.Verify(r => r.ListRecentAsync("t1", 100), Times.Once);

        await ctl.ListRecentTransactions(0);
        repo.Verify(r => r.ListRecentAsync("t1", 100), Times.Exactly(2));
    }
}
