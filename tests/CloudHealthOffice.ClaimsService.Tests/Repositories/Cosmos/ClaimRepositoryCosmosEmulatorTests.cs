using System.Net;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Serialization;
using CloudHealthOffice.Testing.Cosmos;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories.Cosmos;

/// <summary>
/// <see cref="ClaimRepository"/> (the Cosmos implementation) against a real
/// Cosmos DB — the Linux emulator — so the server, not a mocked
/// <see cref="Container"/>, evaluates the conditional writes: the patch
/// <c>FilterPredicate</c>s behind the resolution-lock fence
/// (<c>WithResolutionLockFence</c>), the final-disposition and synchronous
/// write-back guards, and the ETag-pinned final write. Mirrors
/// <see cref="ClaimRepositoryResolutionLockTests"/> (EphemeralMongo) for the
/// same behaviours, plus the status guards the Mongo suite covers elsewhere.
/// The client uses the service's own serializer, so enum literals in the
/// predicates are checked against what is actually persisted.
/// </summary>
[Trait("Category", CosmosEmulator.Category)]
[Collection(CosmosEmulatorFixture.CollectionName)]
public sealed class ClaimRepositoryCosmosEmulatorTests : IAsyncLifetime
{
    private const string Tenant = "tenant-lock";
    private const string ContainerName = "ClaimsV2";
    private static readonly DateTime Now = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly CosmosEmulatorFixture _cosmos;
    private Container _container = null!;
    private IConfiguration _configuration = null!;
    private CosmosClient _client = null!;

    public ClaimRepositoryCosmosEmulatorTests(CosmosEmulatorFixture cosmos) => _cosmos = cosmos;

    // Built per use: HttpContextAccessor keeps the context in an AsyncLocal,
    // which does not flow from InitializeAsync into the test method.
    private ClaimRepository Repo
    {
        get
        {
            var ctx = new DefaultHttpContext();
            ctx.Items["TenantId"] = Tenant;
            return new ClaimRepository(_client, _configuration, new HttpContextAccessor { HttpContext = ctx },
                NullLogger<ClaimRepository>.Instance);
        }
    }

    public async Task InitializeAsync()
    {
        _cosmos.SkipIfUnavailable();
        // The serializer AddChoDatabase registers for claims-service.
        _client = _cosmos.CreateClient(new CosmosSystemTextJsonSerializer());
        var database = await _cosmos.CreateDatabaseAsync(_client, "claims");
        _container = await CosmosEmulatorFixture.CreateContainerAsync(database, ContainerName);
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CosmosDb:DatabaseName"] = database.Id,
            ["CosmosDb:ContainerName"] = ContainerName,
        }).Build();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Claim> SeedAsync(
        string id = "C1",
        ClaimStatus status = ClaimStatus.Pended,
        AdjudicationResult? adjudication = null)
    {
        return await Repo.CreateAsync(new Claim
        {
            Id = id, ClaimVersionId = id, TenantId = Tenant, ClaimNumber = "CN-" + id, MemberId = "M1",
            BillingProviderNPI = "1234567890", Status = status,
            VersionState = ClaimRepository.MapStatusToVersionState(status),
            ServiceDateFrom = Now, ServiceDateTo = Now,
            AdjudicationResult = adjudication,
        });
    }

    private async Task<Claim> StoredAsync(string id = "C1") => (await Repo.GetByIdAsync(id))!;

    /// <summary>The raw document, to check the literal the serializer actually persisted.</summary>
    private async Task<JsonElement> RawAsync(string id = "C1")
    {
        using var response = await _container.ReadItemStreamAsync(id, new PartitionKey(Tenant));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await JsonDocument.ParseAsync(response.Content);
        return document.RootElement.Clone();
    }

    // ── Resolution lock (mirrors ClaimRepositoryResolutionLockTests) ─────

    [SkippableFact]
    public async Task OneResolutionAtATime_UntilTheLockExpires()
    {
        await SeedAsync();
        (await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10))).Should().BeTrue();
        (await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t2", "examiner-2", Now.AddMinutes(1), TimeSpan.FromMinutes(10))).Should().BeFalse();
        (await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t2", "examiner-2", Now.AddMinutes(11), TimeSpan.FromMinutes(10))).Should().BeTrue();
        (await StoredAsync()).ResolutionLock!.Token.Should().Be("t2");
    }

    [SkippableFact]
    public async Task ResolutionLock_IsOnlyTakenOnAPendedClaim()
    {
        await SeedAsync(status: ClaimStatus.Approved);
        (await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10))).Should().BeFalse();
        (await Repo.TryAcquireResolutionLockAsync(Tenant, "missing", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10))).Should().BeFalse();
        (await StoredAsync()).ResolutionLock.Should().BeNull();
    }

    [SkippableFact]
    public async Task FinalWrite_IsFencedOnTheLockToken()
    {
        await SeedAsync();
        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10));
        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t2", "examiner-2", Now.AddMinutes(11), TimeSpan.FromMinutes(10));
        var claim = await StoredAsync();
        claim.Status = ClaimStatus.Approved;
        claim.ResolutionLock = null;

        (await Repo.UpdateHoldingResolutionLockAsync(claim, "t1")).Should().BeNull();
        (await StoredAsync()).Status.Should().Be(ClaimStatus.Pended);

        (await Repo.UpdateHoldingResolutionLockAsync(claim, "t2")).Should().NotBeNull();
        var stored = await StoredAsync();
        stored.Status.Should().Be(ClaimStatus.Approved);
        stored.ResolutionLock.Should().BeNull();
    }

    /// <summary>
    /// The token check and the replace are one atomic step (ETag): a write
    /// that lands between the repository's read and its replace refuses it.
    /// </summary>
    [SkippableFact]
    public async Task FinalWrite_LosesToAConcurrentWrite_BetweenItsReadAndItsReplace()
    {
        await SeedAsync();
        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10));
        var claim = await StoredAsync();
        claim.Status = ClaimStatus.Approved;

        // Reads pass through; the replace first lets another writer in.
        var racing = Substitute.For<Container>();
        racing.ReadItemAsync<Claim>(default!, default, default, default).ReturnsForAnyArgs(ci =>
            _container.ReadItemAsync<Claim>(ci.ArgAt<string>(0), ci.ArgAt<PartitionKey>(1), ci.ArgAt<ItemRequestOptions>(2), ci.ArgAt<CancellationToken>(3)));
        racing.ReplaceItemAsync<Claim>(default!, default!, default, default, default).ReturnsForAnyArgs(async ci =>
        {
            await _container.PatchItemAsync<Claim>("C1", new PartitionKey(Tenant),
                [PatchOperation.Set("/lastUpdatedBy", "someone-else")]);
            return await _container.ReplaceItemAsync(ci.ArgAt<Claim>(0), ci.ArgAt<string>(1), ci.ArgAt<PartitionKey?>(2),
                ci.ArgAt<ItemRequestOptions>(3), ci.ArgAt<CancellationToken>(4));
        });
        var repo = RepoOver(racing);

        (await repo.UpdateHoldingResolutionLockAsync(claim, "t1")).Should().BeNull();
        var stored = await StoredAsync();
        stored.Status.Should().Be(ClaimStatus.Pended);
        stored.LastUpdatedBy.Should().Be("someone-else");
    }

    private Task<bool> RerunWrites(decimal paid, string lockToken) =>
        Repo.UpdateAdjudicationProjectionAsync(
            Tenant, "C1", new AdjudicationResult { PayerPayment = paid, AllowedAmount = paid }, [],
            resolvedStatus: ClaimStatus.Approved, requiredResolutionLockToken: lockToken);

    [SkippableFact]
    public async Task ApprovalRerunWrite_AfterTheLockWasTakenOverAndFinalized_IsRefused()
    {
        await SeedAsync();
        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10));
        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t2", "examiner-2", Now.AddMinutes(11), TimeSpan.FromMinutes(10));
        (await RerunWrites(58m, "t2")).Should().BeTrue();
        var claim = await StoredAsync();
        claim.Status = ClaimStatus.Approved;
        claim.VersionState = ClaimRepository.MapStatusToVersionState(ClaimStatus.Approved);
        claim.ResolutionLock = null;
        (await Repo.UpdateHoldingResolutionLockAsync(claim, "t2")).Should().NotBeNull();

        (await RerunWrites(112m, "t1")).Should().BeFalse();

        var stored = await StoredAsync();
        stored.Status.Should().Be(ClaimStatus.Approved);
        stored.AdjudicationResult!.PayerPayment.Should().Be(58m);
    }

    [SkippableFact]
    public async Task StaleApprovalRerunWrite_BetweenTheNewResolversRerunAndReread_IsRefused()
    {
        await SeedAsync();
        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10));
        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t2", "examiner-2", Now.AddMinutes(11), TimeSpan.FromMinutes(10));
        (await RerunWrites(58m, "t2")).Should().BeTrue();

        (await RerunWrites(112m, "t1")).Should().BeFalse();

        (await StoredAsync()).AdjudicationResult!.PayerPayment.Should().Be(58m);
    }

    /// <summary>
    /// The fenced re-run's status write (<c>TryPatchStatusAsync</c> with the
    /// lock token): the projection patch went through under the live token,
    /// but the claim is Pended, which the synchronous write-back guard
    /// protects — so the status stays Pended for the examiner's own final
    /// write to resolve.
    /// </summary>
    [SkippableFact]
    public async Task FencedRerun_UnderTheLiveToken_WritesTheProjection_ButNotOverAPend()
    {
        await SeedAsync();
        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10));

        (await RerunWrites(58m, "t1")).Should().BeTrue();

        var stored = await StoredAsync();
        stored.AdjudicationResult!.PayerPayment.Should().Be(58m);
        stored.Status.Should().Be(ClaimStatus.Pended);
        stored.ResolutionLock!.Token.Should().Be("t1");
    }

    [SkippableFact]
    public async Task HoldsResolutionLock_OnlyForTheLiveTokenOnAPendedClaim()
    {
        await SeedAsync();
        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10));
        (await Repo.HoldsResolutionLockAsync(Tenant, "C1", "t1", Now.AddMinutes(1))).Should().BeTrue();
        (await Repo.HoldsResolutionLockAsync(Tenant, "C1", "t1", Now.AddMinutes(11))).Should().BeFalse();

        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t2", "examiner-2", Now.AddMinutes(11), TimeSpan.FromMinutes(10));
        (await Repo.HoldsResolutionLockAsync(Tenant, "C1", "t1", Now.AddMinutes(12))).Should().BeFalse();
        (await Repo.HoldsResolutionLockAsync(Tenant, "C1", "t2", Now.AddMinutes(12))).Should().BeTrue();
    }

    [SkippableFact]
    public async Task ReleaseResolutionLock_OnlyReleasesTheHoldersToken()
    {
        await SeedAsync();
        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10));

        await Repo.ReleaseResolutionLockAsync(Tenant, "C1", "someone-else");
        (await StoredAsync()).ResolutionLock!.Token.Should().Be("t1");

        await Repo.ReleaseResolutionLockAsync(Tenant, "C1", "t1");
        (await StoredAsync()).ResolutionLock.Should().BeNull();
        await Repo.ReleaseResolutionLockAsync(Tenant, "missing", "t1"); // gone: no throw

        // Released, so a new resolver can take it before the old expiry.
        (await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t2", "examiner-2", Now.AddMinutes(1), TimeSpan.FromMinutes(10))).Should().BeTrue();
    }

    // ── Final-disposition guard on the async Pend projection ─────────────

    /// <summary>
    /// Approved maps to the Adjudicated version state, which re-adjudication
    /// may still patch — so the Pend projection reaches the row, and only the
    /// conditional status patch (FinalDisposition predicate) keeps Approved.
    /// </summary>
    [SkippableFact]
    public async Task PendProjection_NeverDowngradesAFinalDisposition()
    {
        const ClaimStatus finalStatus = ClaimStatus.Approved;
        await SeedAsync(status: finalStatus);

        var written = await Repo.UpdateAdjudicationProjectionAsync(
            Tenant, "C1", new AdjudicationResult { AllowedAmount = 40m }, [],
            pendDetails: new PendDetails { PendCode = "NCCI" }, isPend: true);

        written.Should().BeTrue("the projection itself is still written");
        var stored = await StoredAsync();
        stored.Status.Should().Be(finalStatus);
        stored.AdjudicationResult!.AllowedAmount.Should().Be(40m);
        stored.PendDetails!.PendCode.Should().Be("NCCI");
    }

    [SkippableTheory]
    [InlineData(ClaimStatus.Submitted)]
    [InlineData(ClaimStatus.Pended)]
    public async Task PendProjection_PendsAClaimWithNoFinalDisposition(ClaimStatus status)
    {
        await SeedAsync(status: status);

        (await Repo.UpdateAdjudicationProjectionAsync(
            Tenant, "C1", new AdjudicationResult(), [], isPend: true)).Should().BeTrue();

        (await StoredAsync()).Status.Should().Be(ClaimStatus.Pended);
        (await RawAsync()).GetProperty("status").GetString().Should().Be("pended", "the predicates bind the camelCase literal");
    }

    [SkippableFact]
    public async Task FencedPendProjection_WithATakenOverToken_WritesNothing()
    {
        await SeedAsync();
        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t1", "examiner-1", Now, TimeSpan.FromMinutes(10));
        await Repo.TryAcquireResolutionLockAsync(Tenant, "C1", "t2", "examiner-2", Now.AddMinutes(11), TimeSpan.FromMinutes(10));

        (await Repo.UpdateAdjudicationProjectionAsync(
            Tenant, "C1", new AdjudicationResult { AllowedAmount = 99m }, [], isPend: true,
            requiredResolutionLockToken: "t1")).Should().BeFalse();

        (await StoredAsync()).AdjudicationResult.Should().BeNull();
    }

    [SkippableTheory]
    [InlineData(ClaimStatus.Paid)]
    [InlineData(ClaimStatus.Denied)]
    [InlineData(ClaimStatus.Voided)]
    public async Task Projection_OfATerminalVersion_IsRefused(ClaimStatus terminal)
    {
        await SeedAsync(status: terminal);

        (await Repo.UpdateAdjudicationProjectionAsync(
            Tenant, "C1", new AdjudicationResult { AllowedAmount = 1m }, [], isPend: true)).Should().BeFalse();
        var stored = await StoredAsync();
        stored.Status.Should().Be(terminal);
        stored.AdjudicationResult.Should().BeNull();
    }

    // ── Synchronous write-back guard (TryTransitionStatusAsync / summary) ─

    [SkippableFact]
    public async Task TransitionStatus_OnAnOpenClaim_IsApplied()
    {
        await SeedAsync(status: ClaimStatus.Submitted);

        var result = await Repo.TryTransitionStatusAsync(Tenant, "C1", ClaimStatus.Denied);

        result.Should().Be(new StatusWriteResult(StatusWriteOutcome.Applied, ClaimStatus.Denied));
        var raw = await RawAsync();
        raw.GetProperty("status").GetString().Should().Be("denied");
        raw.GetProperty("versionState").GetString().Should().Be("denied");
    }

    [SkippableTheory]
    [InlineData(ClaimStatus.Pended)]
    [InlineData(ClaimStatus.Approved)]
    [InlineData(ClaimStatus.Denied)]
    [InlineData(ClaimStatus.Paid)]
    [InlineData(ClaimStatus.Voided)]
    public async Task TransitionStatus_OverAPendOrFinalDisposition_IsSuppressed(ClaimStatus guarded)
    {
        await SeedAsync(status: guarded);
        var desired = guarded == ClaimStatus.Approved ? ClaimStatus.Denied : ClaimStatus.Approved;

        var result = await Repo.TryTransitionStatusAsync(Tenant, "C1", desired);

        result.Should().Be(new StatusWriteResult(StatusWriteOutcome.Suppressed, guarded));
        (await StoredAsync()).Status.Should().Be(guarded);
    }

    [SkippableFact]
    public async Task TransitionStatus_OfAMissingClaim_IsNotFound()
    {
        (await Repo.TryTransitionStatusAsync(Tenant, "missing", ClaimStatus.Approved))
            .Should().Be(StatusWriteResult.NotFoundResult);
    }

    [SkippableFact]
    public async Task AdjudicationSummary_OverAPend_KeepsThePend_ButRecordsTheFinancials()
    {
        await SeedAsync(status: ClaimStatus.Pended);

        var result = await Repo.UpdateAdjudicationSummaryAsync(
            Tenant, "C1", new AdjudicationResult { PayerPayment = 80m, AllowedAmount = 100m }, ClaimStatus.Approved);

        result.Should().Be(new StatusWriteResult(StatusWriteOutcome.Suppressed, ClaimStatus.Pended));
        var stored = await StoredAsync();
        stored.Status.Should().Be(ClaimStatus.Pended);
        stored.AdjudicationResult!.PayerPayment.Should().Be(80m);
    }

    [SkippableFact]
    public async Task AdjudicationSummary_OnAnOpenClaim_SetsStatusAndAdjudicatedState()
    {
        await SeedAsync(status: ClaimStatus.InAdjudication);

        var result = await Repo.UpdateAdjudicationSummaryAsync(
            Tenant, "C1", new AdjudicationResult { PayerPayment = 80m, AllowedAmount = 100m }, ClaimStatus.Approved);

        result.Should().Be(new StatusWriteResult(StatusWriteOutcome.Applied, ClaimStatus.Approved));
        var stored = await StoredAsync();
        stored.Status.Should().Be(ClaimStatus.Approved);
        stored.VersionState.Should().Be(ClaimVersionState.Adjudicated);
    }

    /// <summary>
    /// The narrow repair: a row stuck at "Denied + paid + no denial reason"
    /// is corrected by a paid, reason-free Approved summary.
    /// </summary>
    [SkippableFact]
    public async Task AdjudicationSummary_RepairsAContradictoryDenial()
    {
        await SeedAsync(status: ClaimStatus.Denied, adjudication: new AdjudicationResult { PayerPayment = 50m });

        var result = await Repo.UpdateAdjudicationSummaryAsync(
            Tenant, "C1", new AdjudicationResult { PayerPayment = 50m, AllowedAmount = 60m }, ClaimStatus.Approved);

        result.Should().Be(new StatusWriteResult(StatusWriteOutcome.Applied, ClaimStatus.Approved));
        (await StoredAsync()).Status.Should().Be(ClaimStatus.Approved);
    }

    /// <summary>An unpaid Approved summary is no evidence against a denial: the denial stands.</summary>
    [SkippableFact]
    public async Task AdjudicationSummary_UnpaidApproval_DoesNotReopenADenial()
    {
        await SeedAsync(status: ClaimStatus.Denied,
            adjudication: new AdjudicationResult { PayerPayment = 0m, DenialReasonCode = "CO-50" });

        var result = await Repo.UpdateAdjudicationSummaryAsync(
            Tenant, "C1", new AdjudicationResult { PayerPayment = 0m }, ClaimStatus.Approved);

        result.Should().Be(new StatusWriteResult(StatusWriteOutcome.Suppressed, ClaimStatus.Denied));
        (await StoredAsync()).Status.Should().Be(ClaimStatus.Denied);
    }

    /// <summary>
    /// The inverse repair (Approved + unpaid + denial evidence). Its
    /// predicate uses <c>ARRAY_LENGTH</c>, which the vnext emulator does not
    /// evaluate inside a patch <c>FilterPredicate</c> (400 "The filter
    /// predicate could not be evaluated"); Azure Cosmos DB does. Skipped on
    /// that emulator gap rather than asserted, and runs again unchanged once
    /// the emulator supports it.
    /// </summary>
    [SkippableFact]
    public async Task AdjudicationSummary_RepairsAContradictoryApproval()
    {
        await SeedAsync(status: ClaimStatus.Approved, adjudication: new AdjudicationResult { PayerPayment = 0m });

        StatusWriteResult result;
        try
        {
            result = await Repo.UpdateAdjudicationSummaryAsync(
                Tenant, "C1", new AdjudicationResult { PayerPayment = 0m, DenialReasonCode = "CO-50" }, ClaimStatus.Denied);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.BadRequest
                                          && ex.Message.Contains("filter predicate could not be evaluated", StringComparison.OrdinalIgnoreCase))
        {
            Skip.If(true, "Cosmos emulator gap: ARRAY_LENGTH is not supported in a patch FilterPredicate.");
            return;
        }

        result.Should().Be(new StatusWriteResult(StatusWriteOutcome.Applied, ClaimStatus.Denied));
        (await StoredAsync()).Status.Should().Be(ClaimStatus.Denied);
    }

    // ── Void / supersede projections ─────────────────────────────────────

    [SkippableFact]
    public async Task VoidAndSupersede_Projections_PatchTheRow()
    {
        await SeedAsync(id: "C1", status: ClaimStatus.Approved);
        await SeedAsync(id: "C2", status: ClaimStatus.Paid);

        (await Repo.MarkVoidedProjectionAsync(Tenant, "C1", Now, "actor-1")).Should().BeTrue();
        (await Repo.MarkSupersededProjectionAsync(Tenant, "C2", "C3", Now, "actor-1")).Should().BeTrue();
        (await Repo.MarkVoidedProjectionAsync(Tenant, "missing", Now, "actor-1")).Should().BeFalse();

        var voided = await StoredAsync("C1");
        voided.Status.Should().Be(ClaimStatus.Voided);
        voided.VersionState.Should().Be(ClaimVersionState.Voided);
        var superseded = await StoredAsync("C2");
        superseded.VersionState.Should().Be(ClaimVersionState.Adjusted);
        superseded.SupersededByVersionId.Should().Be("C3");

        // Voided is a final disposition: a late Pend projection cannot reopen it.
        (await Repo.TryTransitionStatusAsync(Tenant, "C1", ClaimStatus.Pended)).Outcome.Should().Be(StatusWriteOutcome.Suppressed);
    }

    // ── Tenant isolation ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnotherTenantsWrites_NeverReachTheRow()
    {
        await SeedAsync(status: ClaimStatus.Submitted);

        (await Repo.TryTransitionStatusAsync("other-tenant", "C1", ClaimStatus.Approved)).Should().Be(StatusWriteResult.NotFoundResult);
        (await Repo.TryAcquireResolutionLockAsync("other-tenant", "C1", "t1", "x", Now, TimeSpan.FromMinutes(1))).Should().BeFalse();
        (await Repo.UpdateAdjudicationProjectionAsync("other-tenant", "C1", new AdjudicationResult(), [], isPend: true)).Should().BeFalse();
        (await StoredAsync()).Status.Should().Be(ClaimStatus.Submitted);
    }

    private ClaimRepository RepoOver(Container container)
    {
        var client = Substitute.For<CosmosClient>();
        client.GetContainer(Arg.Any<string>(), Arg.Any<string>()).Returns(container);
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        return new ClaimRepository(client, _configuration, new HttpContextAccessor { HttpContext = ctx },
            NullLogger<ClaimRepository>.Instance);
    }
}
