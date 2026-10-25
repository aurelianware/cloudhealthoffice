using CoverageService.Models;
using CoverageService.Repositories;
using CoverageService.Services;
using MongoDB.Driver;

namespace CoverageService.Tests.Services;

public class PcpTransactionPolicyTests
{
    [Fact]
    public async Task NativeCounter_WithoutTransaction_RefusesAnUnprotectedRead()
    {
        var repo = new Mock<ICoverageRepository>();
        var counter = new MongoPcpPanelCounter(repo.Object, new PcpMongoSession());
        await ((Func<Task>)(() => counter.CurrentPanelCountAsync("tenant", "1234567890")))
            .Should().ThrowAsync<PanelCountUnavailableException>();
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NativeCounter_CountsDistinctMedicalMembers_WithTenantScope()
    {
        var repo = new Mock<ICoverageRepository>();
        repo.Setup(r => r.GetByPcpNpiAsync("tenant", "1234567890", CoverageStatus.Active, null))
            .ReturnsAsync(new List<Coverage>
            {
                new() { MemberId = "one", InsuranceLineCode = InsuranceLineCodes.Health },
                new() { MemberId = "one", InsuranceLineCode = InsuranceLineCodes.Health },
                new() { MemberId = "dental", InsuranceLineCode = InsuranceLineCodes.Dental },
            });
        var context = new PcpMongoSession { Current = new Mock<IClientSessionHandle>().Object };
        (await new MongoPcpPanelCounter(repo.Object, context).CurrentPanelCountAsync("tenant", "1234567890"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Session_IsAsyncFlowLocal_NotSharedBetweenConcurrentRequests()
    {
        var context = new PcpMongoSession();
        using var first = new Mock<IClientSessionHandle>().Object;
        using var second = new Mock<IClientSessionHandle>().Object;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        async Task Check(IClientSessionHandle session)
        {
            context.Current = session;
            if (Interlocked.Increment(ref entered) == 2) ready.TrySetResult(true);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            context.Current.Should().BeSameAs(session);
            context.Current = null;
        }
        await Task.WhenAll(Task.Run(() => Check(first)), Task.Run(() => Check(second)));
        context.Current.Should().BeNull();
    }
}
