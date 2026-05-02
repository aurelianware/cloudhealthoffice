using System.Net;
using ClaimsService.Models;
using ClaimsService.Models.Migrations;
using ClaimsService.Services.Migrations;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Migrations;

/// <summary>
/// Capability 5.1b — Cosmos partition-key migration service tests.
/// Mocked-Container approach (Decision: contract-level tests, no
/// Cosmos Emulator); covers the migration lifecycle: empty,
/// single-tenant, multi-tenant, legacy hydration, idempotency,
/// dry-run, error paths.
/// </summary>
public class ClaimMigrationServiceTests
{
    private readonly Container _source = Substitute.For<Container>();
    private readonly Container _target = Substitute.For<Container>();
    private readonly ClaimMigrationService _sut;

    public ClaimMigrationServiceTests()
    {
        var resolver = new StubResolver(_source, _target);
        var options = new SingleValueOptionsMonitor<ClaimMigrationOptions>(new ClaimMigrationOptions
        {
            MigrationsEnabled = true,
            SourceContainerName = "Claims",
            TargetContainerName = "ClaimsV2",
            BatchSize = 100,
        });
        _sut = new ClaimMigrationService(resolver, options, NullLogger<ClaimMigrationService>.Instance);
    }

    [Fact]
    public async Task RunAsync_EmptySource_ReturnsZeroCounters()
    {
        StubSourceQuery(Array.Empty<Claim>());
        StubTargetExistenceQuery(new HashSet<string>());

        var result = await _sut.RunAsync(new ClaimMigrationRequest { DryRun = false });

        result.DocumentsRead.Should().Be(0);
        result.DocumentsWritten.Should().Be(0);
        result.DocumentsSkipped.Should().Be(0);
        result.DocumentsErrored.Should().Be(0);
        result.Outcome.Should().Be("success");
        result.SourceContainer.Should().Be("Claims");
        result.TargetContainer.Should().Be("ClaimsV2");
    }

    [Fact]
    public async Task RunAsync_SingleTenant_WritesEachClaimWithTenantPartitionKey()
    {
        var tenantId = "tenant-a";
        var claims = new[]
        {
            MakeClaim("claim-1", tenantId, claimVersionId: "claim-1", versionNumber: 1, state: ClaimVersionState.Submitted),
            MakeClaim("claim-2", tenantId, claimVersionId: "claim-2", versionNumber: 1, state: ClaimVersionState.Paid),
        };
        StubSourceQuery(claims);
        StubTargetExistenceQuery(new HashSet<string>());
        StubTargetCreateOk();

        var result = await _sut.RunAsync(new ClaimMigrationRequest { DryRun = false });

        result.DocumentsRead.Should().Be(2);
        result.DocumentsWritten.Should().Be(2);
        result.DocumentsSkipped.Should().Be(0);
        result.DocumentsErrored.Should().Be(0);

        await _target.Received(1).CreateItemAsync(
            Arg.Is<Claim>(c => c.Id == "claim-1"),
            Arg.Is<PartitionKey>(p => p == new PartitionKey(tenantId)),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
        await _target.Received(1).CreateItemAsync(
            Arg.Is<Claim>(c => c.Id == "claim-2"),
            Arg.Is<PartitionKey>(p => p == new PartitionKey(tenantId)),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_MultiTenant_GroupsExistenceCheckByTenantPartition()
    {
        var claims = new[]
        {
            MakeClaim("claim-1", "tenant-a"),
            MakeClaim("claim-2", "tenant-b"),
            MakeClaim("claim-3", "tenant-a"),
        };
        StubSourceQuery(claims);
        StubTargetExistenceQuery(new HashSet<string>());
        StubTargetCreateOk();

        var result = await _sut.RunAsync(new ClaimMigrationRequest { DryRun = false });

        result.DocumentsRead.Should().Be(3);
        result.DocumentsWritten.Should().Be(3);

        // Existence check executes once per tenant the batch spans.
        _target.Received().GetItemQueryIterator<string>(
            Arg.Any<QueryDefinition>(),
            Arg.Any<string>(),
            Arg.Is<QueryRequestOptions>(o => o.PartitionKey == new PartitionKey("tenant-a")));
        _target.Received().GetItemQueryIterator<string>(
            Arg.Any<QueryDefinition>(),
            Arg.Any<string>(),
            Arg.Is<QueryRequestOptions>(o => o.PartitionKey == new PartitionKey("tenant-b")));
    }

    [Fact]
    public async Task RunAsync_LegacyRowMissingClaimVersionId_HydratesBeforeWriting()
    {
        var claim = new Claim
        {
            Id = "legacy-1",
            TenantId = "tenant-a",
            ClaimVersionId = string.Empty,
            VersionNumber = 0,
            VersionState = ClaimVersionState.Unknown,
            Status = ClaimStatus.Paid,
        };
        StubSourceQuery(new[] { claim });
        StubTargetExistenceQuery(new HashSet<string>());
        StubTargetCreateOk();

        var result = await _sut.RunAsync(new ClaimMigrationRequest { DryRun = false });

        result.DocumentsRead.Should().Be(1);
        result.DocumentsWritten.Should().Be(1);
        result.DocumentsHydrated.Should().Be(1);

        await _target.Received(1).CreateItemAsync(
            Arg.Is<Claim>(c =>
                c.Id == "legacy-1"
                && c.ClaimVersionId == "legacy-1"
                && c.VersionNumber == 1
                && c.VersionState == ClaimVersionState.Paid),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AlreadyMigratedRow_IsSkipped()
    {
        var claim = MakeClaim("claim-1", "tenant-a");
        StubSourceQuery(new[] { claim });
        StubTargetExistenceQuery(new HashSet<string> { "claim-1" });

        var result = await _sut.RunAsync(new ClaimMigrationRequest { DryRun = false });

        result.DocumentsRead.Should().Be(1);
        result.DocumentsWritten.Should().Be(0);
        result.DocumentsSkipped.Should().Be(1);

        await _target.DidNotReceive().CreateItemAsync(
            Arg.Any<Claim>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_DryRun_DoesNotInvokeCreateItem()
    {
        var claim = MakeClaim("claim-1", "tenant-a");
        StubSourceQuery(new[] { claim });
        StubTargetExistenceQuery(new HashSet<string>());

        var result = await _sut.RunAsync(new ClaimMigrationRequest { DryRun = true });

        result.DocumentsRead.Should().Be(1);
        result.DocumentsWritten.Should().Be(1);
        result.DryRun.Should().BeTrue();
        result.Outcome.Should().Be("success");

        await _target.DidNotReceive().CreateItemAsync(
            Arg.Any<Claim>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_TargetWriteFails_RecordsIssueAndReportsPartial()
    {
        var claim = MakeClaim("claim-1", "tenant-a");
        StubSourceQuery(new[] { claim });
        StubTargetExistenceQuery(new HashSet<string>());

        _target.CreateItemAsync(
            Arg.Any<Claim>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("forbidden", HttpStatusCode.Forbidden, 0, "", 0));

        var result = await _sut.RunAsync(new ClaimMigrationRequest { DryRun = false });

        result.DocumentsRead.Should().Be(1);
        result.DocumentsWritten.Should().Be(0);
        result.DocumentsErrored.Should().Be(1);
        result.Outcome.Should().Be("partial");
        result.Issues.Should().ContainSingle(i =>
            i.ClaimId == "claim-1" && i.TenantId == "tenant-a" && i.Outcome == "errored");
    }

    [Fact]
    public async Task RunAsync_ConflictOnTargetWrite_TreatsAsSkipped()
    {
        var claim = MakeClaim("claim-1", "tenant-a");
        StubSourceQuery(new[] { claim });
        StubTargetExistenceQuery(new HashSet<string>());

        _target.CreateItemAsync(
            Arg.Any<Claim>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("conflict", HttpStatusCode.Conflict, 0, "", 0));

        var result = await _sut.RunAsync(new ClaimMigrationRequest { DryRun = false });

        result.DocumentsRead.Should().Be(1);
        result.DocumentsSkipped.Should().Be(1);
        result.DocumentsErrored.Should().Be(0);
        result.Outcome.Should().Be("success");
    }

    [Fact]
    public async Task RunAsync_RowMissingTenantId_RecordsErrorAndDoesNotWrite()
    {
        var claim = new Claim
        {
            Id = "orphan-1",
            TenantId = string.Empty,
        };
        StubSourceQuery(new[] { claim });
        StubTargetExistenceQuery(new HashSet<string>());

        var result = await _sut.RunAsync(new ClaimMigrationRequest { DryRun = false });

        result.DocumentsErrored.Should().Be(1);
        result.Outcome.Should().Be("partial");
        result.Issues.Should().ContainSingle(i => i.ClaimId == "orphan-1");

        await _target.DidNotReceive().CreateItemAsync(
            Arg.Any<Claim>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_DoubleInvocation_ReturnsConflictOnSecondCall()
    {
        // First call hangs by stalling the source iterator's ReadNextAsync;
        // second call must bail with InvalidOperationException so the
        // controller can map to 409 Conflict.
        var firstReady = new TaskCompletionSource<FeedResponse<Claim>>();
        StubSourceQueryDeferred(firstReady.Task);
        StubTargetExistenceQuery(new HashSet<string>());

        var first = _sut.RunAsync(new ClaimMigrationRequest { DryRun = false });

        await Assert.ThrowsAsync<MigrationAlreadyRunningException>(
            () => _sut.RunAsync(new ClaimMigrationRequest { DryRun = false }));

        // Release the first run.
        firstReady.SetResult(MakeFeedResponse(Array.Empty<Claim>()));
        await first;
    }

    [Fact]
    public void GetStatus_ReflectsConfiguredOptions()
    {
        var status = _sut.GetStatus();

        status.MigrationsEnabled.Should().BeTrue();
        status.SourceContainer.Should().Be("Claims");
        status.TargetContainer.Should().Be("ClaimsV2");
        status.BatchSize.Should().Be(100);
        status.IsRunning.Should().BeFalse();
        status.LastRun.Should().BeNull();
    }

    [Fact]
    public async Task GetStatus_AfterRun_ContainsLastRunSummary()
    {
        StubSourceQuery(Array.Empty<Claim>());
        StubTargetExistenceQuery(new HashSet<string>());

        await _sut.RunAsync(new ClaimMigrationRequest { DryRun = true });

        var status = _sut.GetStatus();
        status.LastRun.Should().NotBeNull();
        status.LastRun!.Outcome.Should().Be("success");
        status.IsRunning.Should().BeFalse();
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static Claim MakeClaim(
        string id,
        string tenantId,
        string? claimVersionId = null,
        int versionNumber = 1,
        ClaimVersionState state = ClaimVersionState.Submitted) => new()
    {
        Id = id,
        TenantId = tenantId,
        ClaimVersionId = claimVersionId ?? id,
        VersionNumber = versionNumber,
        VersionState = state,
        Status = ClaimStatus.Submitted,
        MemberId = "member-1",
        ClaimNumber = $"CN-{id}",
    };

    private static FeedResponse<T> MakeFeedResponse<T>(IEnumerable<T> items)
    {
        var list = items.ToList();
        var response = Substitute.For<FeedResponse<T>>();
        response.GetEnumerator().Returns(_ => list.GetEnumerator());
        return response;
    }

    private void StubSourceQuery(IEnumerable<Claim> claims)
    {
        var response = MakeFeedResponse(claims);
        var iterator = Substitute.For<FeedIterator<Claim>>();
        iterator.HasMoreResults.Returns(true, false);
        iterator.ReadNextAsync(Arg.Any<CancellationToken>()).Returns(response);

        _source.GetItemQueryIterator<Claim>(
            Arg.Any<QueryDefinition>(),
            Arg.Any<string>(),
            Arg.Any<QueryRequestOptions>())
            .Returns(iterator);
    }

    private void StubSourceQueryDeferred(Task<FeedResponse<Claim>> first)
    {
        var iterator = Substitute.For<FeedIterator<Claim>>();
        iterator.HasMoreResults.Returns(true, false);
        iterator.ReadNextAsync(Arg.Any<CancellationToken>()).Returns(_ => first);

        _source.GetItemQueryIterator<Claim>(
            Arg.Any<QueryDefinition>(),
            Arg.Any<string>(),
            Arg.Any<QueryRequestOptions>())
            .Returns(iterator);
    }

    private void StubTargetExistenceQuery(HashSet<string> existingIds)
    {
        var response = MakeFeedResponse<string>(existingIds);
        var iterator = Substitute.For<FeedIterator<string>>();
        iterator.HasMoreResults.Returns(true, false);
        iterator.ReadNextAsync(Arg.Any<CancellationToken>()).Returns(response);

        _target.GetItemQueryIterator<string>(
            Arg.Any<QueryDefinition>(),
            Arg.Any<string>(),
            Arg.Any<QueryRequestOptions>())
            .Returns(iterator);
    }

    private void StubTargetCreateOk()
    {
        var response = Substitute.For<ItemResponse<Claim>>();
        _target.CreateItemAsync(
            Arg.Any<Claim>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(response);
    }

    private sealed class StubResolver : IClaimMigrationContainerResolver
    {
        public StubResolver(Container source, Container target)
        {
            Source = source;
            Target = target;
        }
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
