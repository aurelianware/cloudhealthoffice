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
/// <see cref="ClaimRepository"/> queries that filter on enum-valued fields.
/// The Cosmos serializer persists enums camelCase (<c>"draft"</c>), so every
/// bound literal must include that spelling. Before the fix,
/// <see cref="ClaimRepository.GetLatestVersionAsync"/> compared
/// <c>versionState != "Draft"</c>, which let a draft row through as the head,
/// and <see cref="ClaimRepository.UpdateAdjudicationProjectionAsync"/>
/// matched <c>versionState = "Submitted"</c>, which no versioned row
/// satisfied.
/// </summary>
public sealed class ClaimRepositoryVersionStateCosmosTests
{
    private const string TenantId = "tenant-a";

    private readonly Container _container = Substitute.For<Container>();
    private readonly ClaimRepository _sut;

    /// <summary>Every query the repository issued, in order (any result type).</summary>
    private List<QueryDefinition> _captured => _container.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == nameof(Container.GetItemQueryIterator))
        .Select(c => c.GetArguments().OfType<QueryDefinition>().FirstOrDefault())
        .Where(q => q is not null)
        .Select(q => q!)
        .ToList();

    public ClaimRepositoryVersionStateCosmosTests()
    {
        var cosmos = Substitute.For<CosmosClient>();
        cosmos.GetContainer(Arg.Any<string>(), Arg.Any<string>()).Returns(_container);

        var config = Substitute.For<IConfiguration>();
        config["CosmosDb:DatabaseName"].Returns("ClaimsDB");
        config["CosmosDb:ContainerName"].Returns("ClaimsV2");

        var context = new DefaultHttpContext();
        context.Items["TenantId"] = TenantId;
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);

        StubIterator<Claim>();
        StubIterator<ClaimRepository.HeadIdResult>();
        StubIterator<dynamic>();
        StubIterator<int>();

        _sut = new ClaimRepository(cosmos, config, accessor, NullLogger<ClaimRepository>.Instance);
    }

    private void StubIterator<T>()
    {
        // A hand-written iterator: Castle can't proxy FeedIterator<T> over
        // the repository's internal HeadIdResult projection.
        var iterator = new EmptyFeedIterator<T>();
        _container
            .GetItemQueryIterator<T>(
                Arg.Any<QueryDefinition>(),
                Arg.Any<string>(),
                Arg.Any<QueryRequestOptions>())
            .Returns(iterator);
    }

    /// <summary>Serializes a claim exactly as the production Cosmos client would.</summary>
    private static JsonElement Persisted(Claim claim)
    {
        var serializer = new CosmosSystemTextJsonSerializer();
        using var stream = serializer.ToStream(claim);
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    private static string PersistedVersionState(ClaimVersionState state) =>
        Persisted(new Claim { VersionState = state }).GetProperty("versionState").GetString()!;

    private static List<object> ValuesWithPrefix(QueryDefinition q, string prefix) =>
        q.GetQueryParameters().Where(p => p.Name.StartsWith(prefix)).Select(p => p.Value).ToList();

    private static void AssertEveryParameterReferenced(QueryDefinition q)
    {
        foreach (var (name, _) in q.GetQueryParameters())
            q.QueryText.Should().Contain(name);
    }

    [Fact]
    public void Serializer_persists_version_state_camelCase()
    {
        // The premise of the fix: the options-level camelCase enum converter
        // wins over the type-level JsonStringEnumConverter.
        PersistedVersionState(ClaimVersionState.Draft).Should().Be("draft");
        ClaimRepository.CosmosEnumLiteral(ClaimVersionState.Draft).Should().Be("draft");
        ClaimRepository.CosmosEnumLiteral(ClaimStatus.PartiallyPaid)
            .Should().Be(Persisted(new Claim { Status = ClaimStatus.PartiallyPaid }).GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetLatestVersion_excludes_drafts_by_their_persisted_spelling()
    {
        await _sut.GetLatestVersionAsync("chain-1", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var q = _captured.Single();
        ValuesWithPrefix(q, "@draft").Should()
            .Contain(PersistedVersionState(ClaimVersionState.Draft))
            .And.Contain("Draft", "PascalCase rows from older writers are excluded too");
        q.QueryText.Should().Contain("NOT (c.versionState IN (");
        q.QueryText.Should().NotContain("c.versionState != @draft");
        AssertEveryParameterReferenced(q);
    }

    [Fact]
    public async Task UpdateAdjudicationSummary_excludes_drafts_by_their_persisted_spelling()
    {
        await _sut.UpdateAdjudicationSummaryAsync(TenantId, "chain-1", new AdjudicationResult(), ClaimStatus.Approved);

        var q = _captured.First();
        ValuesWithPrefix(q, "@draft").Should().Contain(PersistedVersionState(ClaimVersionState.Draft));
        AssertEveryParameterReferenced(q);
    }

    [Fact]
    public async Task UpdateAdjudicationProjection_matches_adjudicatable_states_by_their_persisted_spelling()
    {
        await _sut.UpdateAdjudicationProjectionAsync(
            TenantId, "chain-1", new AdjudicationResult(), Array.Empty<LineAdjudicationResult>());

        var q = _captured.Single();
        ValuesWithPrefix(q, "@adjudicatableState").Should().Contain(new object[]
        {
            PersistedVersionState(ClaimVersionState.Submitted),
            PersistedVersionState(ClaimVersionState.Adjudicated),
            PersistedVersionState(ClaimVersionState.Unknown),
        });
        ValuesWithPrefix(q, "@adjudicatableState").Should()
            .NotContain(new object[] { "draft", "Draft", "paid", "voided" });
        AssertEveryParameterReferenced(q);
    }

    [Fact]
    public async Task AccumulatorTotals_count_status_and_version_state_by_their_persisted_spelling()
    {
        await _sut.GetAccumulatorTotalsAsync("M-1", "Individual", "plan-1", "2026");

        var q = _captured.Single();
        ValuesWithPrefix(q, "@countedStatus").Should()
            .Contain(new object[] { "approved", "partiallyPaid", "paid" });
        ValuesWithPrefix(q, "@countedVersionState").Should()
            .Contain(new object[]
            {
                PersistedVersionState(ClaimVersionState.Adjudicated),
                PersistedVersionState(ClaimVersionState.Paid),
            });
        q.QueryText.Should().NotContain("'Approved'");
        AssertEveryParameterReferenced(q);
    }

    [Fact]
    public async Task Search_filters_status_and_claim_type_by_their_persisted_spelling()
    {
        await _sut.SearchForMemberAsync(
            "M-1", null, null, ClaimStatus.PartiallyPaid, null, ClaimType.Institutional,
            null, null, 1, 25);

        _captured.Should().NotBeEmpty();
        foreach (var q in _captured)
        {
            ValuesWithPrefix(q, "@status").Should().Contain("partiallyPaid");
            ValuesWithPrefix(q, "@claimType").Should().Contain(
                Persisted(new Claim { ClaimType = ClaimType.Institutional }).GetProperty("claimType").GetString());
            AssertEveryParameterReferenced(q);
        }
    }

    [Fact]
    public async Task Summary_counts_statuses_by_their_persisted_spelling()
    {
        await _sut.GetClaimsSummaryAsync(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            null);

        var q = _captured.First();
        q.QueryText.Should().Contain("c.status IN ('approved', 'Approved')");
        q.QueryText.Should().Contain("c.status IN ('denied', 'Denied')");
        q.QueryText.Should().NotContain("c.status = 'Approved'");
    }
}

internal sealed class EmptyFeedIterator<T> : FeedIterator<T>
{
    public override bool HasMoreResults => false;

    public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No results.");
}
