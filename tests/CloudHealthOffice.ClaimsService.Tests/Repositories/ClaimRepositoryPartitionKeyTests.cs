using System.Net;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// Capability 5.1b — captured-call assertions that every Cosmos
/// PartitionKey site in <see cref="ClaimRepository"/> uses
/// <c>new PartitionKey(tenantId)</c> rather than the legacy
/// <c>claim.Id</c> / <c>id</c> / <c>rowId</c> / <c>claimId</c> values.
///
/// <para>
/// Contract-level coverage (no Cosmos Emulator). Each test sets up
/// the minimum mocked Container interactions to drive a single
/// repository call path and verifies <c>Received()</c> with
/// <c>PartitionKey == new PartitionKey(TenantId)</c>.
/// </para>
/// </summary>
public sealed class ClaimRepositoryPartitionKeyTests
{
    private const string TenantId = "tenant-a";
    private const string ClaimId = "claim-1";

    private readonly Container _container = Substitute.For<Container>();
    private readonly ClaimRepository _sut;

    public ClaimRepositoryPartitionKeyTests()
    {
        var cosmos = Substitute.For<CosmosClient>();
        cosmos.GetContainer(Arg.Any<string>(), Arg.Any<string>()).Returns(_container);

        var config = Substitute.For<IConfiguration>();
        config["CosmosDb:DatabaseName"].Returns("ClaimsDB");
        config["CosmosDb:ContainerName"].Returns("Claims");

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = TenantId;
        httpContextAccessor.HttpContext.Returns(httpContext);

        _sut = new ClaimRepository(cosmos, config, httpContextAccessor, NullLogger<ClaimRepository>.Instance);
    }

    [Fact]
    public async Task GetByIdAsync_UsesTenantPartitionKey()
    {
        StubReadItemReturnsClaim(MakeClaim());

        await _sut.GetByIdAsync(ClaimId);

        await _container.Received(1).ReadItemAsync<Claim>(
            ClaimId,
            new PartitionKey(TenantId),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIdAsync_WithoutHttpContext_UsesAdjudicationTenantPartitionKey()
    {
        var cosmos = Substitute.For<CosmosClient>();
        cosmos.GetContainer(Arg.Any<string>(), Arg.Any<string>()).Returns(_container);

        var config = Substitute.For<IConfiguration>();
        config["CosmosDb:DatabaseName"].Returns("ClaimsDB");
        config["CosmosDb:ContainerName"].Returns("Claims");

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var tenantContext = new AdjudicationTenantContext { TenantId = TenantId };
        var sut = new ClaimRepository(
            cosmos,
            config,
            httpContextAccessor,
            NullLogger<ClaimRepository>.Instance,
            tenantContext);
        StubReadItemReturnsClaim(MakeClaim());

        await sut.GetByIdAsync(ClaimId);

        await _container.Received(1).ReadItemAsync<Claim>(
            ClaimId,
            new PartitionKey(TenantId),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIdAsync_WrongTenant_ReturnsNullDespitePartitionScope()
    {
        // Defense-in-depth check: even though the partition-keyed read
        // would normally be scoped, retain the in-memory tenant guard.
        var foreign = MakeClaim();
        foreign.TenantId = "tenant-b";
        StubReadItemReturnsClaim(foreign);

        var result = await _sut.GetByIdAsync(ClaimId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_UsesTenantPartitionKey()
    {
        StubCreateItemOk();

        await _sut.CreateAsync(MakeClaim());

        await _container.Received(1).CreateItemAsync(
            Arg.Any<Claim>(),
            new PartitionKey(TenantId),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_BothReadAndReplaceUseTenantPartitionKey()
    {
        StubReadItemReturnsClaim(MakeClaim());
        StubReplaceItemOk();

        await _sut.UpdateAsync(MakeClaim());

        await _container.Received(1).ReadItemAsync<Claim>(
            Arg.Any<string>(),
            new PartitionKey(TenantId),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
        await _container.Received(1).ReplaceItemAsync(
            Arg.Any<Claim>(),
            Arg.Any<string>(),
            new PartitionKey(TenantId),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_UsesTenantPartitionKey()
    {
        var deleteResponse = Substitute.For<ItemResponse<Claim>>();
        _container.DeleteItemAsync<Claim>(
            Arg.Any<string>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(deleteResponse);

        await _sut.DeleteAsync(ClaimId);

        await _container.Received(1).DeleteItemAsync<Claim>(
            ClaimId,
            new PartitionKey(TenantId),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkSupersededProjectionAsync_BothReadAndPatchUseTenantPartitionKey()
    {
        StubReadItemReturnsClaim(MakeClaim());
        StubPatchItemOk();

        var ok = await _sut.MarkSupersededProjectionAsync(
            TenantId, ClaimId, "supersessor-id", DateTime.UtcNow, actorId: "actor", default);

        ok.Should().BeTrue();
        await _container.Received(1).ReadItemAsync<Claim>(
            ClaimId,
            new PartitionKey(TenantId),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
        await _container.Received(1).PatchItemAsync<Claim>(
            ClaimId,
            new PartitionKey(TenantId),
            Arg.Any<IReadOnlyList<PatchOperation>>(),
            Arg.Any<PatchItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkVoidedProjectionAsync_BothReadAndPatchUseTenantPartitionKey()
    {
        StubReadItemReturnsClaim(MakeClaim());
        StubPatchItemOk();

        var ok = await _sut.MarkVoidedProjectionAsync(
            TenantId, ClaimId, DateTime.UtcNow, actorId: "actor", default);

        ok.Should().BeTrue();
        await _container.Received(1).ReadItemAsync<Claim>(
            ClaimId,
            new PartitionKey(TenantId),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>());
        await _container.Received(1).PatchItemAsync<Claim>(
            ClaimId,
            new PartitionKey(TenantId),
            Arg.Any<IReadOnlyList<PatchOperation>>(),
            Arg.Any<PatchItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkSupersededProjectionAsync_ForeignTenant_ReturnsFalse()
    {
        // Defense-in-depth: even with the partition-keyed read, the
        // in-memory tenant equality check is intentionally retained.
        // A row whose stored TenantId disagrees with the supplied
        // tenantId is rejected here.
        var foreign = MakeClaim();
        foreign.TenantId = "tenant-b";
        StubReadItemReturnsClaim(foreign);

        var ok = await _sut.MarkSupersededProjectionAsync(
            TenantId, ClaimId, "supersessor-id", DateTime.UtcNow, null, default);

        ok.Should().BeFalse();
        await _container.DidNotReceive().PatchItemAsync<Claim>(
            Arg.Any<string>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<IReadOnlyList<PatchOperation>>(),
            Arg.Any<PatchItemRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static Claim MakeClaim() => new()
    {
        Id = ClaimId,
        TenantId = TenantId,
        ClaimVersionId = ClaimId,
        VersionNumber = 1,
        VersionState = ClaimVersionState.Submitted,
        Status = ClaimStatus.Submitted,
        MemberId = "member-1",
        ClaimNumber = "CN-1",
    };

    private void StubReadItemReturnsClaim(Claim claim)
    {
        var response = Substitute.For<ItemResponse<Claim>>();
        response.Resource.Returns(claim);
        _container.ReadItemAsync<Claim>(
            Arg.Any<string>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(response);
    }

    private void StubCreateItemOk()
    {
        var response = Substitute.For<ItemResponse<Claim>>();
        response.Resource.Returns(MakeClaim());
        _container.CreateItemAsync(
            Arg.Any<Claim>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(response);
    }

    private void StubReplaceItemOk()
    {
        var response = Substitute.For<ItemResponse<Claim>>();
        response.Resource.Returns(MakeClaim());
        _container.ReplaceItemAsync(
            Arg.Any<Claim>(),
            Arg.Any<string>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(response);
    }

    private void StubPatchItemOk()
    {
        var response = Substitute.For<ItemResponse<Claim>>();
        response.Resource.Returns(MakeClaim());
        _container.PatchItemAsync<Claim>(
            Arg.Any<string>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<IReadOnlyList<PatchOperation>>(),
            Arg.Any<PatchItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(response);
    }
}
