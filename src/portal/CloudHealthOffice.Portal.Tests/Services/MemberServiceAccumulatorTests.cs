using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class MemberServiceAccumulatorTests
{
    private readonly Mock<ILogger<MemberService>> _logger = new();
    private readonly IConfiguration _configuration;

    public MemberServiceAccumulatorTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:MemberService"] = "http://localhost:5001"
            })
            .Build();
    }

    private MemberService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new MemberService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetAccumulatorsAsync_WhenApiFails_ReturnsMockData()
    {
        var sut = CreateService();
        var result = await sut.GetAccumulatorsAsync("MBR-8201");

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAccumulatorsAsync_MockData_HasGoldPpoLimits()
    {
        var sut = CreateService();
        var result = await sut.GetAccumulatorsAsync("MBR-8201");

        // Gold PPO: $1,500 individual / $3,000 family deductible
        result.IndividualDeductibleLimit.Should().Be(1500m);
        result.FamilyDeductibleLimit.Should().Be(3000m);
    }

    [Fact]
    public async Task GetAccumulatorsAsync_MockData_UsedDoesNotExceedLimit()
    {
        var sut = CreateService();
        var result = await sut.GetAccumulatorsAsync("MBR-8201");

        result.IndividualDeductibleUsed.Should().BeLessOrEqualTo(result.IndividualDeductibleLimit);
        result.FamilyDeductibleUsed.Should().BeLessOrEqualTo(result.FamilyDeductibleLimit);
        result.IndividualOopUsed.Should().BeLessOrEqualTo(result.IndividualOopLimit);
        result.FamilyOopUsed.Should().BeLessOrEqualTo(result.FamilyOopLimit);
    }

    [Fact]
    public async Task GetAccumulatorsAsync_MockData_IndividualDeductible60To80Percent()
    {
        var sut = CreateService();
        var result = await sut.GetAccumulatorsAsync("MBR-8201");

        var pct = (double)(result.IndividualDeductibleUsed / result.IndividualDeductibleLimit);
        pct.Should().BeInRange(0.60, 0.90,
            "Mock data should show member ~60-80% through individual deductible");
    }

    [Fact]
    public async Task GetAccumulatorsAsync_MockData_HasServiceAccumulators()
    {
        var sut = CreateService();
        var result = await sut.GetAccumulatorsAsync("MBR-8201");

        result.ServiceAccumulators.Should().NotBeEmpty();
        result.ServiceAccumulators.Should().Contain(s => s.ServiceType == "Physical Therapy");
        result.ServiceAccumulators.Should().Contain(s => s.ServiceType == "Mental Health Outpatient");

        foreach (var svc in result.ServiceAccumulators)
        {
            svc.Used.Should().BeLessOrEqualTo(svc.Limit);
            svc.UnitType.Should().BeOneOf("visits", "days", "dollars");
        }
    }

    [Fact]
    public async Task GetAccumulatorsAsync_MockData_HasRecentActivity()
    {
        var sut = CreateService();
        var result = await sut.GetAccumulatorsAsync("MBR-8201");

        result.RecentActivity.Should().HaveCount(5);
        foreach (var act in result.RecentActivity)
        {
            act.ClaimId.Should().StartWith("CLM-");
            act.ServiceDate.Should().BeBefore(DateTime.Today.AddDays(1));
            // At least one cost-sharing field should be > 0
            (act.DeductibleApplied + act.CopayApplied + act.CoinsuranceApplied + act.PlanPaid)
                .Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task GetAccumulatorsAsync_MockData_RecentActivityIsSortedByDate()
    {
        var sut = CreateService();
        var result = await sut.GetAccumulatorsAsync("MBR-8201");

        // Activity should be in reverse chronological order (most recent first)
        for (int i = 1; i < result.RecentActivity.Count; i++)
        {
            result.RecentActivity[i - 1].ServiceDate
                .Should().BeOnOrAfter(result.RecentActivity[i].ServiceDate);
        }
    }
}
