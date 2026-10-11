using System.Text.Json;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.Retention;
using CloudHealthOffice.Testing.Cosmos;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using SharedCosmosSerializer = CloudHealthOffice.Infrastructure.Serialization.CosmosSystemTextJsonSerializer;

namespace AuthorizationService.Tests.Persistence;

/// <summary>
/// Which <see cref="CosmosClient"/> the authorization-service host resolves in
/// Cosmos mode (D-18). No emulator needed: the client is only built, never called.
/// </summary>
public sealed class AuthorizationCosmosClientRegistrationTests
{
    [Fact]
    public void Host_ResolvesTheSharedSystemTextJsonClient()
    {
        using var host = AuthorizationServiceHost.Cosmos("unused", endpoint: "http://localhost:1/");

        _ = host.Services.GetRequiredService<CosmosClient>();

        host.HostClientBuilt.Should().BeTrue();
        host.HostSerializer.Should().BeOfType<SharedCosmosSerializer>(
            "the shared serializer stores enums as strings, the form the repository's queries use");
    }
}

/// <summary>
/// D-18 / G-26: the queries of the authorization repository the host resolves,
/// against a real store. On Cosmos (before the fix) enums were stored as
/// numbers by the host's own client while every query compared them with
/// strings, so search by status or line of business, the summary, the open
/// authorizations the SLA watchdog reads and the retention candidates all
/// came back empty. The same suite runs on MongoDB to show it is unaffected.
/// </summary>
public abstract class AuthorizationRepositoryRealStoreTestsBase : IAsyncLifetime
{
    private static readonly DateTime Base = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    private protected readonly string TenantA = "ra-" + Guid.NewGuid().ToString("N")[..10];
    private protected readonly string TenantB = "rb-" + Guid.NewGuid().ToString("N")[..10];

    private protected AuthorizationServiceHost Host = null!;

    private protected abstract Task<AuthorizationServiceHost> CreateHostAsync();

    private protected virtual Task CleanupAsync() => Task.CompletedTask;

    /// <summary>The Mongo repository has never computed turnaround (D-27).</summary>
    private protected virtual bool ComputesTurnaround => true;

    public async Task InitializeAsync() => Host = await CreateHostAsync();

    public async Task DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
        await CleanupAsync();
    }

    [SkippableFact]
    public async Task Search_ByEachStatus_FindsExactlyThatAuthorization()
    {
        var ids = new Dictionary<AuthorizationStatus, string>();
        foreach (var status in Enum.GetValues<AuthorizationStatus>())
            ids[status] = (await CreateAsync(TenantA, NewAuth(status))).Id;
        // Same statuses in another tenant: never returned to tenant A.
        foreach (var status in Enum.GetValues<AuthorizationStatus>())
            await CreateAsync(TenantB, NewAuth(status));

        foreach (var status in Enum.GetValues<AuthorizationStatus>())
        {
            var found = await AsTenant(TenantA, r => r.SearchAsync(null, null, null, null, status, null, 1, 50));
            found.Select(a => a.Id).Should().Equal(new[] { ids[status] }, $"one {status} authorization was written");
        }
    }

    [SkippableFact]
    public async Task Search_ByLineOfBusiness_FindsOnlyThatLine()
    {
        var medicaid = await CreateAsync(TenantA, NewAuth(AuthorizationStatus.Submitted, LineOfBusiness.Medicaid));
        var tricare = await CreateAsync(TenantA, NewAuth(AuthorizationStatus.Submitted, LineOfBusiness.TRICARE));
        await CreateAsync(TenantA, NewAuth(AuthorizationStatus.Submitted, LineOfBusiness.Commercial));

        (await AsTenant(TenantA, r => r.SearchAsync(null, null, null, null, null, LineOfBusiness.Medicaid, 1, 50)))
            .Select(a => a.Id).Should().Equal(medicaid.Id);
        (await AsTenant(TenantA, r => r.SearchAsync(null, null, null, null, AuthorizationStatus.Submitted, LineOfBusiness.TRICARE, 1, 50)))
            .Select(a => a.Id).Should().Equal(tricare.Id);
    }

    [SkippableFact]
    public async Task Search_ByServiceDateTo_IncludesOpenEndedRequests()
    {
        // No RequestedServiceDateTo: the serializer omits the null property.
        var openEnded = NewAuth(AuthorizationStatus.Submitted);
        openEnded.RequestedServiceDateTo = null;
        var bounded = NewAuth(AuthorizationStatus.Submitted);
        bounded.RequestedServiceDateTo = Base.AddDays(5);
        var tooLate = NewAuth(AuthorizationStatus.Submitted);
        tooLate.RequestedServiceDateTo = Base.AddDays(60);
        await CreateAsync(TenantA, openEnded);
        await CreateAsync(TenantA, bounded);
        await CreateAsync(TenantA, tooLate);

        var found = await AsTenant(TenantA, r => r.SearchAsync(null, null, null, Base.AddDays(30), null, null, 1, 50));

        found.Select(a => a.Id).Should().BeEquivalentTo(new[] { openEnded.Id, bounded.Id });
    }

    [SkippableFact]
    public async Task Summary_CountsEveryStatus_AndFiltersByLineOfBusiness()
    {
        // Reviewed 2 and 4 days after submission: average review 3 days.
        var approved = NewAuth(AuthorizationStatus.Approved);
        approved.ReviewedDate = approved.SubmittedDate.AddDays(2);
        var denied = NewAuth(AuthorizationStatus.Denied);
        denied.ReviewedDate = denied.SubmittedDate.AddDays(4);
        // Resumed a day after submission: turnaround 3 days instead of 4.
        denied.SlaResumedAt = denied.SubmittedDate.AddDays(1);
        await CreateAsync(TenantA, approved);
        await CreateAsync(TenantA, denied);
        await CreateAsync(TenantA, NewAuth(AuthorizationStatus.Approved, LineOfBusiness.Commercial));
        await CreateAsync(TenantA, NewAuth(AuthorizationStatus.Pended));
        await CreateAsync(TenantA, NewAuth(AuthorizationStatus.Modified));
        await CreateAsync(TenantA, NewAuth(AuthorizationStatus.Submitted));
        await CreateAsync(TenantB, NewAuth(AuthorizationStatus.Approved));

        var all = await AsTenant(TenantA, r => r.GetAuthorizationsSummaryAsync(Base.AddDays(-1), Base.AddDays(1), null));

        all.TotalAuthorizations.Should().Be(6);
        all.ApprovedAuthorizations.Should().Be(2);
        all.DeniedAuthorizations.Should().Be(1);
        all.PendedAuthorizations.Should().Be(1);
        all.ModifiedAuthorizations.Should().Be(1);
        all.ApprovalRate.Should().Be(50m);
        all.AverageReviewDays.Should().Be(3m);
        if (ComputesTurnaround)
            all.AverageTurnaroundDays.Should().Be(2.5m);

        var medicaid = await AsTenant(TenantA, r => r.GetAuthorizationsSummaryAsync(Base.AddDays(-1), Base.AddDays(1), LineOfBusiness.Medicaid));

        medicaid.TotalAuthorizations.Should().Be(5);
        medicaid.ApprovedAuthorizations.Should().Be(1);
    }

    [SkippableFact]
    public async Task OpenAuthorizations_AreTheOpenStatuses_ForTheGivenTenantOnly()
    {
        var open = new List<string>();
        foreach (var status in Enum.GetValues<AuthorizationStatus>())
        {
            var created = await CreateAsync(TenantA, NewAuth(status));
            if (status.IsOpen()) open.Add(created.Id);
            await CreateAsync(TenantB, NewAuth(status));
        }

        // Explicit tenant, no request context: how a background job calls it.
        Host.ClearRequestContext();
        using var scope = Host.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>();
        var found = await repository.GetOpenAuthorizationsAsync(TenantA);

        found.Select(a => a.Id).Should().BeEquivalentTo(open);
        found.Should().OnlyContain(a => a.TenantId == TenantA);
    }

    [SkippableFact]
    public async Task RetentionCandidates_AreTheTerminalStatuses_AndTenantsAreListed()
    {
        var terminal = new List<string>();
        foreach (var status in Enum.GetValues<AuthorizationStatus>())
        {
            var created = await CreateAsync(TenantA, NewAuth(status));
            if (!status.IsOpen()) terminal.Add(created.Id);
        }
        await CreateAsync(TenantB, NewAuth(AuthorizationStatus.Denied));

        Host.ClearRequestContext();
        using var scope = Host.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>();

        var candidates = await repository.FindRetentionCandidatesAsync(TenantA, Base.AddDays(1), 100);
        candidates.Select(a => a.Id).Should().BeEquivalentTo(terminal);

        (await repository.ListTenantIdsAsync()).Should().Contain(new[] { TenantA, TenantB });
    }

    [SkippableFact]
    public async Task Enums_RoundTrip()
    {
        var auth = NewAuth(AuthorizationStatus.InReview, LineOfBusiness.VA);
        auth.AuthorizationType = AuthorizationType.ConcurrentReview;
        auth.SlaEscalation = SlaEscalationLevel.Critical;
        auth.StatusHistory.Add(new AuthorizationStatusChange { Status = AuthorizationStatus.InReview, ChangedAt = Base });
        await CreateAsync(TenantA, auth);

        var read = await AsTenant(TenantA, r => r.GetByIdAsync(auth.Id));

        read!.Status.Should().Be(AuthorizationStatus.InReview);
        read.LineOfBusiness.Should().Be(LineOfBusiness.VA);
        read.AuthorizationType.Should().Be(AuthorizationType.ConcurrentReview);
        read.SlaEscalation.Should().Be(SlaEscalationLevel.Critical);
        read.StatusHistory.Should().ContainSingle().Which.Status.Should().Be(AuthorizationStatus.InReview);
    }

    private protected async Task<Authorization> CreateAsync(string tenant, Authorization auth) =>
        await AsTenant(tenant, r => r.CreateAsync(auth));

    private protected async Task<T> AsTenant<T>(string tenant, Func<IAuthorizationRepository, Task<T>> action)
    {
        var (scope, repository) = Host.RepositoryFor(tenant);
        try
        {
            return await action(repository);
        }
        finally
        {
            scope.Dispose();
            Host.ClearRequestContext();
        }
    }

    private protected static Authorization NewAuth(
        AuthorizationStatus status, LineOfBusiness lineOfBusiness = LineOfBusiness.Medicaid)
    {
        var auth = SlaWatchdogRealStoreTestsBase.NewAuthorization("E", status, Base, lineOfBusiness);
        auth.RequestedServiceDateFrom = Base.AddDays(1);
        return auth;
    }
}

/// <summary>The repository suite on MongoDB (the default provider): unaffected by D-18.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AuthorizationRepositoryMongoTests(MongoRunnerFixture mongo) : AuthorizationRepositoryRealStoreTestsBase
{
    private readonly string _databaseName = "as_repo_" + Guid.NewGuid().ToString("N")[..12];

    private protected override bool ComputesTurnaround => false;

    private protected override Task<AuthorizationServiceHost> CreateHostAsync() =>
        Task.FromResult(AuthorizationServiceHost.Mongo(mongo.ConnectionString, _databaseName));

    private protected override Task CleanupAsync() => mongo.Client.DropDatabaseAsync(_databaseName);
}

/// <summary>The repository suite on Cosmos DB, through the host's own client registration.</summary>
[Trait("Category", CosmosEmulator.Category)]
[Collection(CosmosEmulatorFixture.CollectionName)]
public sealed class AuthorizationRepositoryCosmosTests(CosmosEmulatorFixture cosmos) : AuthorizationRepositoryRealStoreTestsBase
{
    private Container _container = null!;

    private protected override async Task<AuthorizationServiceHost> CreateHostAsync()
    {
        cosmos.SkipIfUnavailable();
        var admin = cosmos.CreateClient(new SharedCosmosSerializer());
        var db = await cosmos.CreateDatabaseAsync(admin, "as_d18");
        _container = await CosmosEmulatorFixture.CreateContainerAsync(db, AuthorizationServiceHost.ContainerName);
        return AuthorizationServiceHost.Cosmos(db.Id);
    }

    /// <summary>
    /// The stored document shape (see the D-18 section of
    /// docs/deployment/COSMOS-DB-DEPLOYMENT.md): an <c>id</c>, camelCase
    /// names, enums as strings and no stored nulls.
    /// </summary>
    [SkippableFact]
    public async Task StoredDocument_HasStringEnums_CamelCaseNames_AndNoNulls()
    {
        var auth = NewAuth(AuthorizationStatus.InReview, LineOfBusiness.TRICARE);
        auth.AuthorizationType = AuthorizationType.ConcurrentReview;
        auth.SlaEscalation = SlaEscalationLevel.Warning;
        auth.StatusHistory.Add(new AuthorizationStatusChange { Status = AuthorizationStatus.InReview, ChangedAt = DateTime.UtcNow });
        await CreateAsync(TenantA, auth);

        using var response = await _container.ReadItemStreamAsync(auth.Id, new PartitionKey(TenantA));
        response.IsSuccessStatusCode.Should().BeTrue();
        using var doc = await JsonDocument.ParseAsync(response.Content);
        var root = doc.RootElement;

        root.GetProperty("id").GetString().Should().Be(auth.Id);
        root.GetProperty("tenantId").GetString().Should().Be(TenantA);
        root.GetProperty("status").GetString().Should().Be("inReview");
        root.GetProperty("lineOfBusiness").GetString().Should().Be("tricare");
        root.GetProperty("authorizationType").GetString().Should().Be("concurrentReview");
        // Property-level JsonStringEnumConverter (no naming policy) wins over
        // the serializer's camelCase converter, as it did before.
        root.GetProperty("slaEscalation").GetString().Should().Be("Warning");
        root.GetProperty("statusHistory")[0].GetProperty("status").GetString().Should().Be("InReview");
        root.TryGetProperty("requestedServiceDateTo", out _).Should().BeFalse("nulls are not stored");
        root.TryGetProperty("Status", out _).Should().BeFalse();
    }
}
