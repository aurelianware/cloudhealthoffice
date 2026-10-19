using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.Retention;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AuthorizationService.Tests.Persistence;

/// <summary>
/// D-18 / G-26: the queries of the authorization repository the host resolves,
/// against a real mongod: search by status or line of business, the summary,
/// the open authorizations the SLA watchdog reads, the retention candidates
/// and the conditional SLA escalation write.
/// </summary>
public abstract class AuthorizationRepositoryRealStoreTestsBase : IAsyncLifetime
{
    private protected static readonly DateTime Base = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
            all.AverageTurnaroundDays.Should().Be(3m);

        var medicaid = await AsTenant(TenantA, r => r.GetAuthorizationsSummaryAsync(Base.AddDays(-1), Base.AddDays(1), LineOfBusiness.Medicaid));

        medicaid.TotalAuthorizations.Should().Be(5);
        medicaid.ApprovedAuthorizations.Should().Be(1);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    // ── RaiseSlaEscalationAsync guards (D-07) ────────────────────────────────
    // Each condition of the conditional write is pinned on the real store, so
    // dropping the tenant, open or level condition from either repository fails.

    [Fact]
    public async Task RaiseSlaEscalation_OnOpenRecordBelowTheLevel_Raises()
    {
        var auth = await CreateAsync(TenantA, NewAuth(AuthorizationStatus.Submitted));
        var at = new DateTime(2026, 3, 5, 8, 0, 0, DateTimeKind.Utc);

        (await RaiseAsync(TenantA, auth.Id, SlaEscalationLevel.Warning, at)).Should().BeTrue();

        var stored = await AsTenant(TenantA, r => r.GetByIdAsync(auth.Id));
        stored!.SlaEscalation.Should().Be(SlaEscalationLevel.Warning);
        stored.SlaEscalatedAt.Should().Be(at);
        stored.Status.Should().Be(AuthorizationStatus.Submitted);
        stored.TenantId.Should().Be(TenantA);
    }

    [Fact]
    public async Task RaiseSlaEscalation_ForAnotherTenant_IsRefused()
    {
        var auth = await CreateAsync(TenantA, NewAuth(AuthorizationStatus.Submitted));

        (await RaiseAsync(TenantB, auth.Id, SlaEscalationLevel.Breach)).Should().BeFalse();

        var stored = await AsTenant(TenantA, r => r.GetByIdAsync(auth.Id));
        stored!.SlaEscalation.Should().Be(SlaEscalationLevel.None);
        stored.SlaEscalatedAt.Should().BeNull();
        (await AsTenant(TenantB, r => r.GetByIdAsync(auth.Id))).Should().BeNull();
    }

    [Fact]
    public async Task RaiseSlaEscalation_OnClosedRecord_IsRefused()
    {
        foreach (var status in Enum.GetValues<AuthorizationStatus>().Where(s => !s.IsOpen()))
        {
            var auth = await CreateAsync(TenantA, NewAuth(status));

            (await RaiseAsync(TenantA, auth.Id, SlaEscalationLevel.Breach)).Should().BeFalse($"{status} is closed");

            (await AsTenant(TenantA, r => r.GetByIdAsync(auth.Id)))!.SlaEscalation.Should().Be(SlaEscalationLevel.None);
        }
    }

    [Fact]
    public async Task RaiseSlaEscalation_OnRecordClosedAfterBeingListed_IsRefused()
    {
        var auth = await CreateAsync(TenantA, NewAuth(AuthorizationStatus.InReview));

        Host.ClearRequestContext();
        using (var scope = Host.Services.CreateScope())
        {
            var listed = await scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>()
                .GetOpenAuthorizationsAsync(TenantA);
            listed.Select(a => a.Id).Should().Contain(auth.Id);
        }

        // A reviewer decides it between the watchdog's list and its write.
        await AsTenant(TenantA, async r =>
        {
            var current = await r.GetByIdAsync(auth.Id);
            current!.Status = AuthorizationStatus.Approved;
            return await r.UpdateAsync(current);
        });

        (await RaiseAsync(TenantA, auth.Id, SlaEscalationLevel.Breach)).Should().BeFalse();

        var stored = await AsTenant(TenantA, r => r.GetByIdAsync(auth.Id));
        stored!.Status.Should().Be(AuthorizationStatus.Approved);
        stored.SlaEscalation.Should().Be(SlaEscalationLevel.None);
    }

    [Fact]
    public async Task RaiseSlaEscalation_ToAnEqualOrLowerLevel_IsRefused()
    {
        var auth = NewAuth(AuthorizationStatus.Pended);
        auth.SlaEscalation = SlaEscalationLevel.Critical;
        var escalatedAt = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        auth.SlaEscalatedAt = escalatedAt;
        await CreateAsync(TenantA, auth);

        (await RaiseAsync(TenantA, auth.Id, SlaEscalationLevel.Critical)).Should().BeFalse();
        (await RaiseAsync(TenantA, auth.Id, SlaEscalationLevel.Warning)).Should().BeFalse();

        var stored = await AsTenant(TenantA, r => r.GetByIdAsync(auth.Id));
        stored!.SlaEscalation.Should().Be(SlaEscalationLevel.Critical);
        stored.SlaEscalatedAt.Should().Be(escalatedAt);
    }

    [Fact]
    public async Task RaiseSlaEscalation_InParallel_ExactlyOneWins()
    {
        var auth = await CreateAsync(TenantA, NewAuth(AuthorizationStatus.Submitted));
        Host.ClearRequestContext();

        // Each racer in its own scope, as two watchdog replicas would be.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using var scope = Host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>()
                .RaiseSlaEscalationAsync(TenantA, auth.Id, SlaEscalationLevel.Warning, DateTime.UtcNow);
        })));

        results.Count(r => r).Should().Be(1);
        (await AsTenant(TenantA, r => r.GetByIdAsync(auth.Id)))!.SlaEscalation.Should().Be(SlaEscalationLevel.Warning);
    }

    [Fact]
    public async Task RaiseSlaEscalation_OnMissingRecord_ReturnsFalse()
    {
        (await RaiseAsync(TenantA, Guid.NewGuid().ToString(), SlaEscalationLevel.Warning)).Should().BeFalse();
    }

    /// <summary>The raise exactly as a background caller makes it: explicit tenant, no request.</summary>
    private protected async Task<bool> RaiseAsync(
        string tenant, string id, SlaEscalationLevel level, DateTime? at = null)
    {
        Host.ClearRequestContext();
        using var scope = Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>()
            .RaiseSlaEscalationAsync(tenant, id, level, at ?? DateTime.UtcNow);
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

    [Fact]
    public async Task ListActivityBetween_FindsSubmittedOrDecidedInTheWindow_ForTheTenantOnly()
    {
        // Submitted in the window, still open.
        var submitted = await CreateAsync(TenantA, NewAuth(AuthorizationStatus.Submitted));
        // Submitted before the window, decided inside it.
        var decidedLate = NewAuth(AuthorizationStatus.Approved);
        decidedLate.SubmittedDate = Base.AddDays(-20);
        decidedLate.ReviewedDate = Base.AddDays(1);
        decidedLate = await CreateAsync(TenantA, decidedLate);
        // Submitted and decided before the window.
        var old = NewAuth(AuthorizationStatus.Denied);
        old.SubmittedDate = Base.AddDays(-30);
        old.ReviewedDate = Base.AddDays(-25);
        await CreateAsync(TenantA, old);
        // Same window, other tenant.
        await CreateAsync(TenantB, NewAuth(AuthorizationStatus.Submitted));

        var found = await AsTenant(TenantA, r => r.ListActivityBetweenAsync(Base.AddDays(-1), Base.AddDays(2)));

        found.Select(a => a.Id).Should().BeEquivalentTo(new[] { submitted.Id, decidedLate.Id });
    }

    private protected static Authorization NewAuth(
        AuthorizationStatus status, LineOfBusiness lineOfBusiness = LineOfBusiness.Medicaid)
    {
        var auth = SlaWatchdogRealStoreTestsBase.NewAuthorization("E", status, Base, lineOfBusiness);
        auth.RequestedServiceDateFrom = Base.AddDays(1);
        return auth;
    }
}

/// <summary>The repository suite on MongoDB (EphemeralMongo).</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AuthorizationRepositoryMongoTests(MongoRunnerFixture mongo) : AuthorizationRepositoryRealStoreTestsBase
{
    private readonly string _databaseName = "as_repo_" + Guid.NewGuid().ToString("N")[..12];

    private protected override bool ComputesTurnaround => false;

    private protected override Task<AuthorizationServiceHost> CreateHostAsync() =>
        Task.FromResult(AuthorizationServiceHost.Mongo(mongo.ConnectionString, _databaseName));

    private protected override Task CleanupAsync() => mongo.Client.DropDatabaseAsync(_databaseName);

    /// <summary>
    /// An escalation sets only its two fields; properties this version does
    /// not model (a newer pod's, during a rolling deploy) survive.
    /// </summary>
    [Fact]
    public async Task RaiseSlaEscalation_KeepsUnmodelledProperties()
    {
        var auth = await CreateAsync(TenantA, NewAuth(AuthorizationStatus.Submitted));
        var raw = mongo.Client.GetDatabase(_databaseName).GetCollection<BsonDocument>(AuthorizationServiceHost.ContainerName);
        await raw.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", auth.Id),
            Builders<BsonDocument>.Update.Set("patientReference", "Patient/p-123"));

        (await RaiseAsync(TenantA, auth.Id, SlaEscalationLevel.Warning)).Should().BeTrue();

        var doc = await raw.Find(Builders<BsonDocument>.Filter.Eq("_id", auth.Id)).SingleAsync();
        doc["patientReference"].AsString.Should().Be("Patient/p-123");
        doc["SlaEscalation"].ToInt32().Should().Be((int)SlaEscalationLevel.Warning);
        doc["Status"].ToInt32().Should().Be((int)AuthorizationStatus.Submitted);
    }
}
