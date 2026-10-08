using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// Contract coverage (no Cosmos Emulator) for the Cosmos
/// <see cref="ClaimRepository.FindDuplicateCandidatesAsync"/> query: the
/// dead-row exclusion literals must match the enum spelling the Cosmos
/// serializer actually persists, or dead rows survive the filter and can
/// fill the TOP cap ahead of a live duplicate.
/// </summary>
public sealed class ClaimRepositoryDuplicateCandidatesCosmosTests
{
    private const string TenantId = "tenant-a";

    private readonly Container _container = Substitute.For<Container>();
    private readonly ClaimRepository _sut;
    private QueryDefinition? _captured;
    private QueryRequestOptions? _capturedOptions;

    public ClaimRepositoryDuplicateCandidatesCosmosTests()
    {
        var cosmos = Substitute.For<CosmosClient>();
        cosmos.GetContainer(Arg.Any<string>(), Arg.Any<string>()).Returns(_container);

        var config = Substitute.For<IConfiguration>();
        config["CosmosDb:DatabaseName"].Returns("ClaimsDB");
        config["CosmosDb:ContainerName"].Returns("ClaimsV2");

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);

        var iterator = Substitute.For<FeedIterator<Claim>>();
        iterator.HasMoreResults.Returns(false);
        _container
            .GetItemQueryIterator<Claim>(
                Arg.Do<QueryDefinition>(q => _captured = q),
                Arg.Any<string>(),
                Arg.Do<QueryRequestOptions>(o => _capturedOptions = o))
            .Returns(iterator);

        _sut = new ClaimRepository(cosmos, config, accessor, NullLogger<ClaimRepository>.Instance);
    }

    /// <summary>Serializes a claim exactly as the production Cosmos client would.</summary>
    private static (string Status, string VersionState) PersistedEnumSpelling(
        ClaimStatus status, ClaimVersionState versionState)
    {
        var serializer = new CosmosSystemTextJsonSerializer();
        using var stream = serializer.ToStream(new Claim { Status = status, VersionState = versionState });
        using var doc = JsonDocument.Parse(stream);
        return (doc.RootElement.GetProperty("status").GetString()!,
                doc.RootElement.GetProperty("versionState").GetString()!);
    }

    private async Task<List<(string Name, object Value)>> RunQueryAsync()
    {
        await _sut.FindDuplicateCandidatesAsync(
            TenantId, "M-1",
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc),
            "chain-x");
        _captured.Should().NotBeNull();
        return _captured!.GetQueryParameters().ToList();
    }

    [Theory]
    [InlineData(ClaimStatus.Denied, ClaimVersionState.Denied)]
    [InlineData(ClaimStatus.Voided, ClaimVersionState.Voided)]
    public async Task Excludes_dead_statuses_using_the_persisted_spelling(
        ClaimStatus status, ClaimVersionState versionState)
    {
        var (persistedStatus, persistedVersionState) = PersistedEnumSpelling(status, versionState);

        var parameters = await RunQueryAsync();

        parameters.Where(p => p.Name.StartsWith("@deadStatus")).Select(p => p.Value)
            .Should().Contain(persistedStatus);
        parameters.Where(p => p.Name.StartsWith("@deadVersionState")).Select(p => p.Value)
            .Should().Contain(persistedVersionState);
    }

    [Theory]
    [InlineData(ClaimVersionState.Draft)]
    [InlineData(ClaimVersionState.Adjusted)]
    public async Task Excludes_dead_version_states_using_the_persisted_spelling(ClaimVersionState versionState)
    {
        var (_, persistedVersionState) = PersistedEnumSpelling(ClaimStatus.Submitted, versionState);

        var parameters = await RunQueryAsync();

        parameters.Where(p => p.Name.StartsWith("@deadVersionState")).Select(p => p.Value)
            .Should().Contain(persistedVersionState)
            .And.Contain(versionState.ToString(), "PascalCase rows from older writers are excluded too");
    }

    [Fact]
    public async Task Status_literals_are_camelCase_and_every_parameter_is_referenced()
    {
        var parameters = await RunQueryAsync();

        parameters.Where(p => p.Name.StartsWith("@deadStatus")).Select(p => p.Value)
            .Should().Contain(new object[] { "denied", "voided" });
        foreach (var (name, _) in parameters)
        {
            _captured!.QueryText.Should().Contain(name);
        }
        _captured!.QueryText.Should().Contain($"TOP {ClaimRepository.MaxDuplicateCandidates}");
        _capturedOptions!.PartitionKey.Should().Be(new PartitionKey(TenantId));
    }

    [Fact]
    public async Task Scopes_to_tenant_member_window_and_excluded_chain()
    {
        var parameters = await RunQueryAsync();

        parameters.Should().Contain(("@tenantId", (object)TenantId));
        parameters.Should().Contain(("@memberId", (object)"M-1"));
        parameters.Should().Contain(("@excludeChain", (object)"chain-x"));
    }
}
