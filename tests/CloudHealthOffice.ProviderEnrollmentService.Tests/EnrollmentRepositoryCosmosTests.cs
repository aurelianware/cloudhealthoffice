using System.Net;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.ProviderEnrollmentService.Tests;

public class EnrollmentRepositoryCosmosTests
{
    private readonly Container _container;
    private readonly EnrollmentRepositoryCosmos _sut;

    private const string TestNpi   = "1234567890";
    private const string TestState = "TX";

    public EnrollmentRepositoryCosmosTests()
    {
        var cosmosClient = Substitute.For<CosmosClient>();
        _container = Substitute.For<Container>();

        cosmosClient.GetContainer(Arg.Any<string>(), Arg.Any<string>())
            .Returns(_container);

        var config = Substitute.For<IConfiguration>();
        config["CosmosDb:DatabaseName"].Returns("TestDB");
        config["ProviderEnrollmentService:CacheContainer"].Returns("enrollment-cache");

        var options = Options.Create(new ProviderEnrollmentOptions
        {
            CacheTtl = TimeSpan.FromHours(4)
        });

        _sut = new EnrollmentRepositoryCosmos(
            cosmosClient,
            config,
            options,
            Substitute.For<ILogger<EnrollmentRepositoryCosmos>>());
    }

    private static StateEnrollmentRecord MakeRecord(
        string npi = TestNpi,
        string stateCode = TestState) => new()
    {
        Npi           = npi,
        StateCode     = stateCode,
        SourceSystem  = "PEMS",
        Status        = EnrollmentStatus.Active,
        EffectiveDate = new DateOnly(2023, 1, 15),
        ProviderType  = ProviderTypeClassification.PhysicianMD,
        SupportedLobs = LineOfBusiness.Medicaid | LineOfBusiness.STAR,
        McoParticipation = ["MCO-001"]
    };

    private static EnrollmentCacheDocument MakeDocument(
        string npi = TestNpi,
        string stateCode = TestState) =>
        EnrollmentCacheDocument.FromRecord(MakeRecord(npi, stateCode), TimeSpan.FromHours(4));

    // ── Test 1: GetAsync — document exists ────────────────────────

    [Fact]
    public async Task GetAsync_DocumentExists_ReturnsRecord()
    {
        // Arrange
        var doc = MakeDocument();
        var response = Substitute.For<ItemResponse<EnrollmentCacheDocument>>();
        response.Resource.Returns(doc);

        _container.ReadItemAsync<EnrollmentCacheDocument>(
            Arg.Any<string>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(response);

        // Act
        var result = await _sut.GetAsync(TestNpi, TestState);

        // Assert
        result.Should().NotBeNull();
        result!.Npi.Should().Be(TestNpi);
        result.StateCode.Should().Be(TestState);
        result.Status.Should().Be(EnrollmentStatus.Active);
        result.IsFromCache.Should().BeTrue();
    }

    // ── Test 2: GetAsync — not found ──────────────────────────────

    [Fact]
    public async Task GetAsync_NotFound_ReturnsNull()
    {
        // Arrange
        _container.ReadItemAsync<EnrollmentCacheDocument>(
            Arg.Any<string>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("Not found", HttpStatusCode.NotFound, 0, "", 0));

        // Act
        var result = await _sut.GetAsync(TestNpi, TestState);

        // Assert
        result.Should().BeNull();
    }

    // ── Test 3: UpsertAsync — correct partition key ───────────────

    [Fact]
    public async Task UpsertAsync_CallsContainerUpsert_WithCorrectPartitionKey()
    {
        // Arrange
        var record = MakeRecord();

        _container.UpsertItemAsync<EnrollmentCacheDocument>(
            Arg.Any<EnrollmentCacheDocument>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Substitute.For<ItemResponse<EnrollmentCacheDocument>>());

        // Act
        await _sut.UpsertAsync(record);

        // Assert
        await _container.Received(1).UpsertItemAsync(
            Arg.Is<EnrollmentCacheDocument>(d =>
                d.Npi == TestNpi &&
                d.StateCode == TestState &&
                d.Id == $"{TestNpi}::{TestState}"),
            Arg.Is<PartitionKey>(pk => pk.Equals(new PartitionKey(TestState))),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Test 4: GetProvidersWithRevalidationDueSoonAsync — state scoped

    [Fact]
    public async Task GetProvidersWithRevalidationDueSoonAsync_StateScoped_UsesPartitionKey()
    {
        // Arrange
        var feedIterator = Substitute.For<FeedIterator<EnrollmentCacheDocument>>();
        feedIterator.HasMoreResults.Returns(true, false);

        var feedResponse = Substitute.For<FeedResponse<EnrollmentCacheDocument>>();
        feedResponse.GetEnumerator().Returns(new List<EnrollmentCacheDocument>().GetEnumerator());
        feedIterator.ReadNextAsync(Arg.Any<CancellationToken>()).Returns(feedResponse);

        _container.GetItemQueryIterator<EnrollmentCacheDocument>(
            Arg.Any<QueryDefinition>(),
            Arg.Any<string>(),
            Arg.Is<QueryRequestOptions>(o => o.PartitionKey != null))
            .Returns(feedIterator);

        // Act
        var results = await _sut.GetProvidersWithRevalidationDueSoonAsync(90, "TX");

        // Assert
        results.Should().NotBeNull();
        _container.Received(1).GetItemQueryIterator<EnrollmentCacheDocument>(
            Arg.Any<QueryDefinition>(),
            Arg.Any<string>(),
            Arg.Is<QueryRequestOptions>(o => o.PartitionKey != null));
    }

    // ── Test 5: GetActivePanelByMcoAsync ──────────────────────────

    [Fact]
    public async Task GetActivePanelByMcoAsync_ReturnsMatchingRecords()
    {
        // Arrange
        var doc = MakeDocument();
        var feedIterator = Substitute.For<FeedIterator<EnrollmentCacheDocument>>();
        feedIterator.HasMoreResults.Returns(true, false);

        var feedResponse = Substitute.For<FeedResponse<EnrollmentCacheDocument>>();
        feedResponse.GetEnumerator().Returns(new List<EnrollmentCacheDocument> { doc }.GetEnumerator());
        feedIterator.ReadNextAsync(Arg.Any<CancellationToken>()).Returns(feedResponse);

        _container.GetItemQueryIterator<EnrollmentCacheDocument>(
            Arg.Any<QueryDefinition>(),
            Arg.Any<string>(),
            Arg.Any<QueryRequestOptions>())
            .Returns(feedIterator);

        // Act
        var results = await _sut.GetActivePanelByMcoAsync("TX", "MCO-001");

        // Assert
        results.Should().ContainSingle();
        results[0].Npi.Should().Be(TestNpi);
        results[0].StateCode.Should().Be(TestState);
    }

    // ── Test 6: DeleteAsync — not found is idempotent ─────────────

    [Fact]
    public async Task DeleteAsync_NotFound_IsIdempotent()
    {
        // Arrange
        _container.DeleteItemAsync<EnrollmentCacheDocument>(
            Arg.Any<string>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("Not found", HttpStatusCode.NotFound, 0, "", 0));

        // Act — should NOT throw
        var act = () => _sut.DeleteAsync(TestNpi, TestState);

        // Assert
        await act.Should().NotThrowAsync();
    }
}
