using System.Net;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// Residual-race fix — Cosmos-side coverage for the atomic status-write
/// primitive backing <c>TryTransitionStatusAsync</c> (used by
/// <c>PUT /{id}/adjudication</c> and <c>PUT /{id}/status</c>) and, via the
/// same shared private helper, <c>UpdateAdjudicationSummaryAsync</c>'s status
/// half.
///
/// <para>
/// Contract-level coverage (no Cosmos Emulator — mirrors
/// <see cref="ClaimRepositoryPartitionKeyTests"/>'s established pattern for
/// this repository). <c>TryTransitionStatusAsync</c> operates on a single row
/// by direct id (no version-chain query, unlike <c>UpdateAdjudicationSummaryAsync</c>'s
/// chain lookup, which resolves through a private nested query-result type
/// that isn't reachable from a mocked <see cref="Container"/> in this test
/// project) — so it's the cleanest place to verify the actual Cosmos
/// conditional-patch wiring: that a blocked status is expressed as a patch
/// <c>FilterPredicate</c> the server evaluates atomically (not a prior read),
/// and that a rejected (412) conditional patch is translated into
/// <see cref="StatusWriteOutcome.Suppressed"/> via a fallback read — not
/// silently swallowed or misreported as success.
/// </para>
/// </summary>
public sealed class ClaimRepositoryStatusWritebackGuardTests
{
    private const string TenantId = "tenant-a";
    private const string ClaimId = "claim-1";

    private readonly Container _container = Substitute.For<Container>();
    private readonly ClaimRepository _sut;

    public ClaimRepositoryStatusWritebackGuardTests()
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
    public async Task TryTransitionStatusAsync_NotBlocked_PatchesStatusAndVersionState_WithFilterPredicateSet()
    {
        StubPatchItemOk();

        var result = await _sut.TryTransitionStatusAsync(TenantId, ClaimId, ClaimStatus.Denied);

        result.Outcome.Should().Be(StatusWriteOutcome.Applied);
        result.PersistedStatus.Should().Be(ClaimStatus.Denied);

        // The guard MUST be expressed server-side via FilterPredicate — a
        // conditional patch, not a read-then-decide check — so it holds
        // under a true concurrent write, not just sequential ordering.
        await _container.Received(1).PatchItemAsync<Claim>(
            ClaimId,
            new PartitionKey(TenantId),
            Arg.Is<IReadOnlyList<PatchOperation>>(ops => ops.Count == 2),
            Arg.Is<PatchItemRequestOptions>(o => o != null && !string.IsNullOrEmpty(o.FilterPredicate)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryTransitionStatusAsync_FilterPredicate_ExcludesPendedAndFinalDispositions()
    {
        // Pin the actual predicate content, not just "some predicate was
        // set" — this is what stands between a synchronous write-back and
        // silently overwriting a pend. Built from
        // ClaimRepository.SynchronousWritebackBlockedStatuses so it can't
        // drift from the canonical BlocksSynchronousWriteback rule.
        StubPatchItemOk();
        PatchItemRequestOptions? captured = null;
        _container.PatchItemAsync<Claim>(
            Arg.Any<string>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<IReadOnlyList<PatchOperation>>(),
            Arg.Do<PatchItemRequestOptions>(o => captured = o),
            Arg.Any<CancellationToken>());

        await _sut.TryTransitionStatusAsync(TenantId, ClaimId, ClaimStatus.Approved);

        captured.Should().NotBeNull();
        foreach (var blocked in ClaimRepository.SynchronousWritebackBlockedStatuses)
        {
            captured!.FilterPredicate.Should()
                .Contain($"'{JsonNamingPolicy.CamelCase.ConvertName(blocked.ToString())}'");
            captured.FilterPredicate.Should()
                .NotContain($"'{blocked}'", "Cosmos persists ClaimStatus enum strings as camelCase");
        }
    }

    [Fact]
    public async Task TryTransitionStatusAsync_GuardBlocked_ReturnsSuppressed_WithActualPersistedStatus()
    {
        // Server-side FilterPredicate rejection surfaces as 412; the method
        // must not treat that as a hard failure, and must report what's
        // actually persisted (fetched via a fallback read — a rejected
        // conditional patch doesn't return the document).
        StubPatchItemThrows(new CosmosException("precondition failed", HttpStatusCode.PreconditionFailed, 0, "", 0));
        StubReadItemReturnsClaim(MakeClaim(ClaimStatus.Pended));

        var result = await _sut.TryTransitionStatusAsync(TenantId, ClaimId, ClaimStatus.Approved);

        result.Outcome.Should().Be(StatusWriteOutcome.Suppressed);
        result.PersistedStatus.Should().Be(ClaimStatus.Pended);
    }

    [Fact]
    public async Task TryTransitionStatusAsync_RowMissing_ReturnsNotFound()
    {
        StubPatchItemThrows(new CosmosException("not found", HttpStatusCode.NotFound, 0, "", 0));

        var result = await _sut.TryTransitionStatusAsync(TenantId, ClaimId, ClaimStatus.Approved);

        result.Outcome.Should().Be(StatusWriteOutcome.NotFound);
        result.PersistedStatus.Should().BeNull();
    }

    [Fact]
    public async Task TryTransitionStatusAsync_RowDeletedBetweenBlockedPatchAndFallbackRead_ReturnsNotFound()
    {
        StubPatchItemThrows(new CosmosException("precondition failed", HttpStatusCode.PreconditionFailed, 0, "", 0));
        _container.ReadItemAsync<Claim>(
            Arg.Any<string>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("not found", HttpStatusCode.NotFound, 0, "", 0));

        var result = await _sut.TryTransitionStatusAsync(TenantId, ClaimId, ClaimStatus.Approved);

        result.Outcome.Should().Be(StatusWriteOutcome.NotFound);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static Claim MakeClaim(ClaimStatus status = ClaimStatus.Submitted) => new()
    {
        Id = ClaimId,
        TenantId = TenantId,
        ClaimVersionId = ClaimId,
        VersionNumber = 1,
        VersionState = ClaimVersionState.Submitted,
        Status = status,
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

    private void StubPatchItemThrows(CosmosException ex)
    {
        _container.PatchItemAsync<Claim>(
            Arg.Any<string>(),
            Arg.Any<PartitionKey>(),
            Arg.Any<IReadOnlyList<PatchOperation>>(),
            Arg.Any<PatchItemRequestOptions>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(ex);
    }
}
