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
    [Fact]
    public async Task GetActiveCoverageByMemberId_FiltersOnInForceStatuses_NotActiveOnly()
    {
        QueryDefinition? captured = null;

        var iterator = new Mock<FeedIterator<Coverage>>();
        iterator.Setup(i => i.HasMoreResults).Returns(false);

        var container = new Mock<Container>();
        container
            .Setup(c => c.GetItemQueryIterator<Coverage>(
                It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Callback((QueryDefinition q, string _, QueryRequestOptions _) => captured = q)
            .Returns(iterator.Object);

        var database = new Mock<Database>();
        database.Setup(d => d.GetContainer("Coverage")).Returns(container.Object);
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetDatabase("db")).Returns(database.Object);

        var repo = new CoverageRepository(client.Object, "db");
        await repo.GetActiveCoverageByMemberIdAsync("t1", "M1", new DateTime(2025, 3, 15));

        captured.Should().NotBeNull();
        captured!.QueryText.Should().Contain("ARRAY_CONTAINS(@dosStatuses, c.status)");
        captured.QueryText.Should().NotContain("@activeStatus");

        var statuses = captured.GetQueryParameters()
            .Single(p => p.Name == "@dosStatuses").Value
            .Should().BeAssignableTo<IEnumerable<int>>().Subject;
        statuses.Should().BeEquivalentTo(new[]
        {
            (int)CoverageStatus.Active,
            (int)CoverageStatus.Pending,
            (int)CoverageStatus.Terminated,
            (int)CoverageStatus.COBRA
        });
        statuses.Should().NotContain((int)CoverageStatus.Suspended);
    }
}
