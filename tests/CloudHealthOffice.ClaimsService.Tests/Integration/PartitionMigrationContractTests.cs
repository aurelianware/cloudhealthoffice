using ClaimsService.Models;
using ClaimsService.Models.Migrations;
using ClaimsService.Services.Migrations;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.Integration;

/// <summary>
/// Capability 5.1b — composition-level contract tests reframing the
/// originally-planned <c>PartitionMigrationEndToEndTests</c> away from
/// real-Cosmos round-trip (no Cosmos Emulator in the claims-service
/// test stack) toward mocked <see cref="Container"/> end-to-end:
/// pre-migration mixed legacy + canonical data → migration runs → all
/// rows written to the target with hydrated versioning fields → idempotent
/// rerun produces zero new writes. Real-Cosmos verification is the
/// operator runbook's pre-cutover dev-environment dry-run step.
/// </summary>
public sealed class PartitionMigrationContractTests
{
    [Fact]
    public async Task EndToEnd_MixedLegacyAndCanonicalRows_AllWrittenWithHydratedFields()
    {
        var source = Substitute.For<Container>();
        var target = new InMemoryTargetContainer();

        var canonicalRow = new Claim
        {
            Id = "claim-1",
            TenantId = "tenant-a",
            ClaimVersionId = "claim-1",
            VersionNumber = 1,
            VersionState = ClaimVersionState.Submitted,
            Status = ClaimStatus.Submitted,
        };
        var legacyRow = new Claim
        {
            Id = "legacy-1",
            TenantId = "tenant-a",
            ClaimVersionId = string.Empty,
            VersionNumber = 0,
            VersionState = ClaimVersionState.Unknown,
            Status = ClaimStatus.Paid,
        };
        var differentTenantRow = new Claim
        {
            Id = "claim-2",
            TenantId = "tenant-b",
            ClaimVersionId = "claim-2",
            VersionNumber = 1,
            VersionState = ClaimVersionState.Adjudicated,
            Status = ClaimStatus.Approved,
        };

        StubSourceQuery(source, new[] { canonicalRow, legacyRow, differentTenantRow });

        var sut = BuildSut(source, target);

        var firstRun = await sut.RunAsync(new ClaimMigrationRequest { DryRun = false });

        firstRun.DocumentsRead.Should().Be(3);
        firstRun.DocumentsWritten.Should().Be(3);
        firstRun.DocumentsSkipped.Should().Be(0);
        firstRun.DocumentsHydrated.Should().Be(1);
        firstRun.Outcome.Should().Be("success");

        target.Items.Should().HaveCount(3);
        target.Items["claim-1"].Should().Match<Claim>(c =>
            c.ClaimVersionId == "claim-1"
            && c.VersionNumber == 1
            && c.VersionState == ClaimVersionState.Submitted);
        target.Items["legacy-1"].Should().Match<Claim>(c =>
            c.ClaimVersionId == "legacy-1"
            && c.VersionNumber == 1
            && c.VersionState == ClaimVersionState.Paid);
        target.Items["claim-2"].Should().Match<Claim>(c =>
            c.ClaimVersionId == "claim-2"
            && c.VersionNumber == 1
            && c.VersionState == ClaimVersionState.Adjudicated);

        target.PartitionKeysSeen.Should().Contain(new PartitionKey("tenant-a"));
        target.PartitionKeysSeen.Should().Contain(new PartitionKey("tenant-b"));
    }

    [Fact]
    public async Task EndToEnd_IdempotentRerun_ProducesZeroNewWrites()
    {
        var source = Substitute.For<Container>();
        var target = new InMemoryTargetContainer();

        var rows = new[]
        {
            new Claim
            {
                Id = "claim-1", TenantId = "tenant-a", ClaimVersionId = "claim-1",
                VersionNumber = 1, VersionState = ClaimVersionState.Paid,
                Status = ClaimStatus.Paid,
            },
            new Claim
            {
                Id = "claim-2", TenantId = "tenant-a", ClaimVersionId = "claim-2",
                VersionNumber = 1, VersionState = ClaimVersionState.Submitted,
                Status = ClaimStatus.Submitted,
            },
        };

        StubSourceQuery(source, rows);

        var sut = BuildSut(source, target);

        var firstRun = await sut.RunAsync(new ClaimMigrationRequest { DryRun = false });
        firstRun.DocumentsWritten.Should().Be(2);

        // Re-stub the source — the iterator stub is single-use because
        // HasMoreResults sequence is consumed.
        StubSourceQuery(source, rows);

        var secondRun = await sut.RunAsync(new ClaimMigrationRequest { DryRun = false });

        secondRun.DocumentsRead.Should().Be(2);
        secondRun.DocumentsWritten.Should().Be(0);
        secondRun.DocumentsSkipped.Should().Be(2);
        secondRun.Outcome.Should().Be("success");
        target.Items.Should().HaveCount(2);
    }

    private static ClaimMigrationService BuildSut(Container source, InMemoryTargetContainer target)
    {
        var resolver = new StubResolver(source, target.Container);
        var options = new SingleValueOptionsMonitor<ClaimMigrationOptions>(new ClaimMigrationOptions
        {
            MigrationsEnabled = true,
            SourceContainerName = "Claims",
            TargetContainerName = "ClaimsV2",
            BatchSize = 100,
        });
        return new ClaimMigrationService(resolver, options, NullLogger<ClaimMigrationService>.Instance);
    }

    private static void StubSourceQuery(Container source, IEnumerable<Claim> claims)
    {
        var list = claims.ToList();
        var feedResponse = Substitute.For<FeedResponse<Claim>>();
        feedResponse.GetEnumerator().Returns(_ => list.GetEnumerator());

        var iterator = Substitute.For<FeedIterator<Claim>>();
        iterator.HasMoreResults.Returns(true, false);
        iterator.ReadNextAsync(Arg.Any<CancellationToken>()).Returns(feedResponse);

        source.GetItemQueryIterator<Claim>(
            Arg.Any<QueryDefinition>(),
            Arg.Any<string>(),
            Arg.Any<QueryRequestOptions>())
            .Returns(iterator);
    }

    /// <summary>
    /// Records writes against a <see cref="Container"/> mock, exposes
    /// the in-memory state for assertions, and serves the existence-
    /// check query the migration uses to decide whether to write.
    /// </summary>
    private sealed class InMemoryTargetContainer
    {
        public Container Container { get; }
        public Dictionary<string, Claim> Items { get; } = new(StringComparer.Ordinal);
        public List<PartitionKey> PartitionKeysSeen { get; } = new();

        public InMemoryTargetContainer()
        {
            Container = Substitute.For<Container>();

            Container.CreateItemAsync(
                Arg.Any<Claim>(),
                Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(),
                Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var claim = call.Arg<Claim>();
                    var key = call.Arg<PartitionKey>();
                    PartitionKeysSeen.Add(key);
                    Items[claim.Id] = claim;
                    var response = Substitute.For<ItemResponse<Claim>>();
                    response.Resource.Returns(claim);
                    return Task.FromResult(response);
                });

            Container.GetItemQueryIterator<string>(
                Arg.Any<QueryDefinition>(),
                Arg.Any<string>(),
                Arg.Any<QueryRequestOptions>())
                .Returns(call =>
                {
                    var query = call.Arg<QueryDefinition>();
                    var requestOptions = call.Arg<QueryRequestOptions>();
                    var partitionTenantId = ExtractPartitionTenant(requestOptions);
                    var queriedIds = ExtractIdsParameter(query);

                    var matches = Items.Values
                        .Where(c => c.TenantId == partitionTenantId)
                        .Where(c => queriedIds.Contains(c.Id))
                        .Select(c => c.Id)
                        .ToList();

                    var feedResponse = Substitute.For<FeedResponse<string>>();
                    feedResponse.GetEnumerator().Returns(_ => matches.GetEnumerator());

                    var iterator = Substitute.For<FeedIterator<string>>();
                    iterator.HasMoreResults.Returns(true, false);
                    iterator.ReadNextAsync(Arg.Any<CancellationToken>()).Returns(feedResponse);
                    return iterator;
                });
        }

        private static string ExtractPartitionTenant(QueryRequestOptions? options)
            => options?.PartitionKey?.ToString()?.Trim('[', ']', '"') ?? string.Empty;

        private static HashSet<string> ExtractIdsParameter(QueryDefinition query)
        {
            // QueryDefinition exposes parameters via an enumerable contract;
            // pull the @ids array out so the in-memory store can scope to
            // the IDs the service is asking about.
            foreach (var param in query.GetQueryParameters())
            {
                if (param.Name == "@ids" && param.Value is IEnumerable<string> arr)
                {
                    return new HashSet<string>(arr, StringComparer.Ordinal);
                }
                if (param.Name == "@ids" && param.Value is string[] strArr)
                {
                    return new HashSet<string>(strArr, StringComparer.Ordinal);
                }
            }
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private sealed class StubResolver : IClaimMigrationContainerResolver
    {
        public StubResolver(Container source, Container target) { Source = source; Target = target; }
        public Container Source { get; }
        public Container Target { get; }
    }

    private sealed class SingleValueOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public SingleValueOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
