using CoverageService.Models;
using CoverageService.Repositories;
using Microsoft.Azure.Cosmos;

namespace CoverageService.Tests.Repositories;

/// <summary>
/// The Cosmos date-of-service query must filter on the in-force status set
/// (<see cref="Coverage.DateOfServiceStatuses"/>), not Status == Active, so a
/// terminated coverage is still found for service dates within its span.
/// </summary>
public class CoverageRepositoryCosmosActiveOnDateTests
{
    private sealed class Captured
    {
        public QueryDefinition? Query;
        public QueryRequestOptions? Options;
    }

    private static (CoverageRepository Repo, Captured Captured) Build()
    {
        var captured = new Captured();

        var iterator = new Mock<FeedIterator<Coverage>>();
        iterator.Setup(i => i.HasMoreResults).Returns(false);

        var container = new Mock<Container>();
        container
            .Setup(c => c.GetItemQueryIterator<Coverage>(
                It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Callback((QueryDefinition q, string _, QueryRequestOptions o) =>
            {
                captured.Query = q;
                captured.Options = o;
            })
            .Returns(iterator.Object);

        var database = new Mock<Database>();
        database.Setup(d => d.GetContainer("Coverage")).Returns(container.Object);
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetDatabase("db")).Returns(database.Object);

        return (new CoverageRepository(client.Object, "db"), captured);
    }

    private static object Param(QueryDefinition q, string name) =>
        q.GetQueryParameters().Single(p => p.Name == name).Value;

    [Fact]
    public async Task GetActiveCoverageByMemberId_FiltersOnInForceStatuses_NotActiveOnly()
    {
        var (repo, captured) = Build();
        await repo.GetActiveCoverageByMemberIdAsync("t1", "M1", new DateTime(2025, 3, 15));

        captured.Query.Should().NotBeNull();
        captured.Query!.QueryText.Should().Contain("ARRAY_CONTAINS(@dosStatuses, c.status)");
        captured.Query.QueryText.Should().NotContain("@activeStatus");

        var statuses = Param(captured.Query, "@dosStatuses")
            .Should().BeAssignableTo<IEnumerable<int>>().Subject;
        statuses.Should().BeEquivalentTo(new[]
        {
            (int)CoverageStatus.Active,
            (int)CoverageStatus.Terminated,
            (int)CoverageStatus.COBRA
        });
        statuses.Should().NotContain((int)CoverageStatus.Pending)
            .And.NotContain((int)CoverageStatus.Suspended);
    }

    [Fact]
    public async Task GetActiveCoverageByMemberId_WithInsuranceLine_FiltersOnIt()
    {
        // The clause used to be appended to the text after the QueryDefinition
        // was built, so the filter was silently dropped.
        var (repo, captured) = Build();
        await repo.GetActiveCoverageByMemberIdAsync("t1", "M1", new DateTime(2025, 3, 15), "DEN");

        captured.Query!.QueryText.Should().Contain("AND c.insuranceLineCode = @insuranceLineCode");
        Param(captured.Query, "@insuranceLineCode").Should().Be("DEN");
        Param(captured.Query, "@tenantId").Should().Be("t1");
        Param(captured.Query, "@memberId").Should().Be("M1");
        Param(captured.Query, "@serviceDate").Should().Be(new DateTime(2025, 3, 15));
        captured.Options!.PartitionKey.Should().Be(new PartitionKey("t1"));
    }

    [Fact]
    public async Task GetActiveCoverageByMemberId_WithoutInsuranceLine_HasNoLineFilter()
    {
        var (repo, captured) = Build();
        await repo.GetActiveCoverageByMemberIdAsync("t1", "M1", new DateTime(2025, 3, 15));

        captured.Query!.QueryText.Should().NotContain("insuranceLineCode");
        captured.Query.GetQueryParameters().Select(p => p.Name).Should().NotContain("@insuranceLineCode");
    }

    [Fact]
    public async Task GetActiveCoverageByMemberId_TreatsNullTerminationDateAsOpenEnded()
    {
        // The serializer writes terminationDate: null for open-ended coverage;
        // NOT IS_DEFINED alone is false for null and null >= date is undefined.
        var (repo, captured) = Build();
        await repo.GetActiveCoverageByMemberIdAsync("t1", "M1", new DateTime(2025, 3, 15));

        captured.Query!.QueryText.Should().Contain(
            "(NOT IS_DEFINED(c.terminationDate) OR IS_NULL(c.terminationDate) OR c.terminationDate >= @serviceDate)");
    }

    [Fact]
    public async Task Search_ActiveOnly_ExcludesTerminationDateReached()
    {
        var (repo, captured) = Build();
        await repo.SearchAsync("t1", groupNumber: "G1", activeOnly: true);

        captured.Query!.QueryText.Should().Contain("c.status = @activeStatus")
            .And.Contain("(NOT IS_DEFINED(c.terminationDate) OR IS_NULL(c.terminationDate) OR c.terminationDate > @today)");
        Param(captured.Query, "@activeStatus").Should().Be((int)CoverageStatus.Active);
        Param(captured.Query, "@today").Should().Be(DateTime.UtcNow.Date);
    }

    [Fact]
    public async Task Search_NotActiveOnly_HasNoDateFilter()
    {
        var (repo, captured) = Build();
        await repo.SearchAsync("t1", groupNumber: "G1");

        captured.Query!.QueryText.Should().NotContain("@today").And.NotContain("@activeStatus");
    }

    [Fact]
    public async Task History_ExcludingTerminated_AlsoExcludesTerminationDateReached()
    {
        var (repo, captured) = Build();
        await repo.GetCoverageHistoryAsync("t1", "M1", includeTerminated: false);

        captured.Query!.QueryText.Should().Contain("c.status != @terminatedStatus")
            .And.Contain("c.terminationDate > @today");
        Param(captured.Query, "@terminatedStatus").Should().Be((int)CoverageStatus.Terminated);
        Param(captured.Query, "@today").Should().Be(DateTime.UtcNow.Date);
    }

    [Fact]
    public async Task GetStatusTransitionsDue_QueriesEveryTenantForReachedTerminationDates()
    {
        var (repo, captured) = Build();
        await repo.GetStatusTransitionsDueAsync(new DateTime(2025, 7, 1, 13, 0, 0), 250);

        captured.Query!.QueryText.Should().Contain("SELECT TOP @maxItems")
            .And.Contain("c.status != @terminatedStatus")
            .And.Contain("NOT IS_NULL(c.terminationDate)")
            .And.Contain("c.terminationDate <= @today")
            .And.NotContain("c.tenantId");
        Param(captured.Query, "@maxItems").Should().Be(250);
        Param(captured.Query, "@terminatedStatus").Should().Be((int)CoverageStatus.Terminated);
        Param(captured.Query, "@today").Should().Be(new DateTime(2025, 7, 1));
        // Cross-partition: no partition key.
        captured.Options?.PartitionKey.Should().BeNull();
    }
}
