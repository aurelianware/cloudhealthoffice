using AuthorizationService.Consumers;
using AuthorizationService.Exceptions;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AuthorizationService.Services;

namespace AuthorizationService.Tests.Persistence;

/// <summary>
/// Optimistic concurrency on the MongoDB authorization repository
/// (EphemeralMongo). <c>UpdateAsync</c> replaced the document unconditionally,
/// so of two concurrent writers the later silently erased the earlier, and a
/// replace from a stale read undid an SLA escalation. Every write now bumps
/// <see cref="Authorization.Revision"/> and a replace is conditional on the
/// revision it read; the RFAI consumer re-reads and re-applies on a conflict.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AuthorizationRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-concurrency";
    private const string Collection = "Authorizations";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("auth_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private AuthorizationRepositoryMongo Repo()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        return new AuthorizationRepositoryMongo(
            _db, new ConfigurationBuilder().Build(),
            new HttpContextAccessor { HttpContext = ctx },
            NullLogger<AuthorizationRepositoryMongo>.Instance);
    }

    private async Task<Authorization> SeedAsync(string id, AuthorizationStatus status = AuthorizationStatus.Pended) =>
        await Repo().CreateAsync(new Authorization
        {
            Id = id,
            AuthorizationNumber = "AUTH-" + id,
            MemberId = "MBR-001",
            PatientFirstName = "Jane",
            PatientLastName = "Doe",
            PatientDateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LineOfBusiness = LineOfBusiness.Commercial,
            RequestingProviderNPI = "1234567890",
            AuthorizationType = AuthorizationType.PreAuthorization,
            ServiceTypeCode = "1",
            RequestedServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            Status = status,
            RFAIIssued = true,
            SubmittedDate = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc),
        });

    private async Task<Authorization> ReadAsync(string id) => (await Repo().GetByIdAsync(id))!;

    private static async Task<Exception?> Try(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task RegisteredExpiryWorker_ExpiresTwoTenantsWithoutARequest_AndIsIdempotent()
    {
        var first = await SeedAsync("expiry-worker-a", AuthorizationStatus.Approved);
        first.ExpirationDate = DateTime.UtcNow.AddDays(-10); await Repo().UpdateAsync(first);
        await _db.GetCollection<Authorization>(Collection).InsertOneAsync(new Authorization
        {
            Id = "expiry-worker-b", TenantId = "tenant-b", AuthorizationNumber = "AUTH-worker-b",
            Status = AuthorizationStatus.Modified, ApprovedServiceDateTo = DateTime.UtcNow.AddDays(-10),
        });
        using var host = AuthorizationServiceHost.Mongo(mongo.ConnectionString, _db.DatabaseNamespace.DatabaseName);
        host.ClearRequestContext();
        var worker = host.Services.GetServices<IHostedService>().OfType<AuthorizationExpiryWorker>().Single();
        Assert.Equal(2, await worker.SweepAsync());
        Assert.Equal(0, await worker.SweepAsync());
        var stored = await _db.GetCollection<Authorization>(Collection).Find(a => a.Status == AuthorizationStatus.Expired).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, a => Assert.Equal("authorization-expiry", Assert.Single(a.StatusHistory).ChangedBy));
    }

    [Fact]
    public async Task Expiry_IsAtomicAuditedTenantScoped_AndPreservesUnknownFields()
    {
        var today = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        var auth = await SeedAsync("expiry", AuthorizationStatus.Approved);
        auth.ApprovedServiceDateTo = today.AddDays(-1);
        await Repo().UpdateAsync(auth);
        var raw = _db.GetCollection<BsonDocument>(Collection);
        await raw.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", auth.Id),
            Builders<BsonDocument>.Update.Set("futureData", "preserved").Set("StatusHistory", BsonNull.Value));
        var first = await ReadAsync(auth.Id); var second = await ReadAsync(auth.Id);
        Assert.Empty(await Repo().FindExpiryCandidatesAsync("other-tenant", today, 200));
        Assert.Single(await Repo().FindExpiryCandidatesAsync(Tenant, today, 200));
        var results = await Task.WhenAll(Repo().ExpireIfStillEligibleAsync(Tenant, first, today, today),
            Repo().ExpireIfStillEligibleAsync(Tenant, second, today, today));
        Assert.Single(results, r => r);
        var stored = await ReadAsync(auth.Id);
        Assert.Equal(AuthorizationStatus.Expired, stored.Status);
        Assert.Equal("A1", stored.ReviewDecision);
        var ended = await Repo().ListApprovedEndingBetweenAsync(today.AddDays(-30), today);
        Assert.Contains(ended, a => a.Id == stored.Id);
        var summary = await Repo().GetAuthorizationsSummaryAsync(stored.SubmittedDate.AddDays(-1), stored.SubmittedDate.AddDays(1), null);
        Assert.Equal(100m, summary.ApprovalRate);
        Assert.Equal(1, summary.ExpiredAuthorizations);
        Assert.Equal("authorization-expiry", Assert.Single(stored.StatusHistory).Source);
        Assert.Equal("preserved", (await raw.Find(Builders<BsonDocument>.Filter.Eq("_id", auth.Id)).SingleAsync())["futureData"].AsString);
    }

    [Fact]
    public async Task ExpiryCannotOverwriteAConcurrentPeriodChangeOrCancellation_AndKeepsLastDayValid()
    {
        var today = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        var auth = await SeedAsync("expiry-race", AuthorizationStatus.Modified);
        auth.ExpirationDate = today.AddDays(-1); await Repo().UpdateAsync(auth);
        var stale = await ReadAsync(auth.Id);
        auth.ExpirationDate = today; await Repo().UpdateAsync(auth);
        Assert.False(await Repo().ExpireIfStillEligibleAsync(Tenant, stale, today, today));
        Assert.Empty(await Repo().FindExpiryCandidatesAsync(Tenant, today, 200));
        var current = await ReadAsync(auth.Id);
        current.ExpirationDate = today.AddDays(-2); await Repo().UpdateAsync(current);
        stale = await ReadAsync(auth.Id);
        current.Status = AuthorizationStatus.Cancelled; await Repo().UpdateAsync(current);
        Assert.False(await Repo().ExpireIfStillEligibleAsync(Tenant, stale, today, today));
        Assert.Equal(AuthorizationStatus.Cancelled, (await ReadAsync(auth.Id)).Status);
    }

    [Fact]
    public async Task LegacyDeadlineInitialization_IsTenantAndRevisionFenced_AndOneConcurrentWriterWins()
    {
        var auth = await SeedAsync("legacy-deadline", AuthorizationStatus.Pended);
        var collection = _db.GetCollection<Authorization>(Collection);
        await collection.UpdateOneAsync(a => a.Id == auth.Id, Builders<Authorization>.Update
            .Unset(a => a.DecisionDueAt).Unset(a => a.DecisionReceivedAt).Unset(a => a.DecisionDeadlinePolicy));
        var first = await ReadAsync(auth.Id);
        var second = await ReadAsync(auth.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => Repo().EnsureDecisionDeadlineAsync("other-tenant", first));
        var results = await Task.WhenAll(Repo().EnsureDecisionDeadlineAsync(Tenant, first), Repo().EnsureDecisionDeadlineAsync(Tenant, second));
        Assert.Single(results, r => r);
        var stored = await ReadAsync(auth.Id);
        Assert.Equal(stored.SubmittedDate, stored.DecisionReceivedAt);
        Assert.Equal(stored.SubmittedDate.AddDays(7), stored.DecisionDueAt);
        stored.SlaResumedAt = stored.SubmittedDate.AddDays(10);
        await Repo().UpdateAsync(stored);
        var reloaded = await ReadAsync(auth.Id);
        Assert.Equal(stored.DecisionDueAt, reloaded.DecisionDueAt);
    }

    [Fact]
    public async Task MixedDecision_SurvivesMongoReloadWithCodedLineReasonsAndHistory()
    {
        var authorization = await SeedAsync("mixed-lines", AuthorizationStatus.Submitted);
        authorization.RequestedServices = new()
        {
            new() { LineNumber = 7, ProcedureCode = "72148", RequestedUnits = 4 },
            new() { LineNumber = 12, ProcedureCode = "72148", RequestedUnits = 2 },
        };
        await Repo().UpdateAsync(authorization);
        var backend = new global::AuthorizationService.Backends.ChoAuthorizationBackend(Repo());
        await backend.UpdateStatusAsync(authorization, AuthorizationStatus.Modified, "A2", null,
            details: new() { LineDecisions = new()
            {
                new() { LineNumber = 7, ReviewDecision = "A1" },
                new() { LineNumber = 12, ReviewDecision = "A3", DenialReasonCode = "NOTMEDNEC", DenialReason = "Clinical criteria not met." },
            }});
        var stored = await ReadAsync("mixed-lines");
        Assert.Equal(AuthorizationStatus.Modified, stored.Status);
        Assert.Equal(4, stored.RequestedServices[0].ApprovedUnits);
        Assert.Equal(0, stored.RequestedServices[1].ApprovedUnits);
        Assert.Equal("NOTMEDNEC", stored.RequestedServices[1].DenialReasonCode);
        Assert.Equal(12, stored.StatusHistory.Last().LineDecisions[1].LineNumber);
        Assert.Equal("Clinical criteria not met.", stored.StatusHistory.Last().LineDecisions[1].DenialReason);
    }

    [Fact]
    public async Task RealHost_RefusesToStartWithLegacyDuplicateNumbers()
    {
        var collection = _db.GetCollection<Authorization>(Collection);
        await collection.InsertManyAsync([
            new Authorization { Id = "legacy-host-a", TenantId = Tenant, AuthorizationNumber = "AUTH-DUP" },
            new Authorization { Id = "legacy-host-b", TenantId = Tenant, AuthorizationNumber = "AUTH-DUP" },
        ]);
        using var host = AuthorizationServiceHost.Mongo(mongo.ConnectionString, _db.DatabaseNamespace.DatabaseName);
        var start = () => host.CreateClient();
        start.Should().Throw<MongoCommandException>();
        (await collection.CountDocumentsAsync(Builders<Authorization>.Filter.Empty)).Should().Be(2);
    }

    [Fact]
    public async Task IndexInitialization_RefusesLegacyDuplicatesWithoutDeletingOrRenumberingThem()
    {
        var collection = _db.GetCollection<Authorization>(Collection);
        await collection.InsertManyAsync([
            new Authorization { Id = "legacy-a", TenantId = Tenant, AuthorizationNumber = "AUTH-LEGACY" },
            new Authorization { Id = "legacy-b", TenantId = Tenant, AuthorizationNumber = "AUTH-LEGACY" },
        ]);
        var initialize = () => Repo().EnsureIndexesAsync();
        await initialize.Should().ThrowAsync<MongoCommandException>();
        (await collection.CountDocumentsAsync(Builders<Authorization>.Filter.Empty)).Should().Be(2);
    }

    [Fact]
    public async Task IndexInitialization_CreatesTheUniqueNumberAndTenantStatusIndexes()
    {
        await Repo().EnsureIndexesAsync();
        var indexes = await (await _db.GetCollection<BsonDocument>(Collection).Indexes.ListAsync()).ToListAsync();
        indexes.Should().Contain(i => i["name"] == "authorization_tenant_number_unique" && i["unique"].AsBoolean);
        indexes.Should().Contain(i => i["name"] == "authorization_tenant_status");
        indexes.Should().Contain(i => i["name"] == "authorization_tenant_status_expiration");
        indexes.Should().Contain(i => i["name"] == "authorization_tenant_status_service_end");
    }

    [Fact]
    public async Task ConcurrentCreates_WithTheSameTenantAndNumber_StoreExactlyOneRecord()
    {
        var first = new Authorization { Id = "create-a", AuthorizationNumber = "AUTH-UNIQUE" };
        var second = new Authorization { Id = "create-b", AuthorizationNumber = "AUTH-UNIQUE" };
        var results = await Task.WhenAll(
            Try(() => Repo().CreateAsync(first)), Try(() => Repo().CreateAsync(second)));

        results.Count(e => e is null).Should().Be(1);
        results.Count(e => e is CloudHealthOffice.Infrastructure.Data.ConcurrencyConflictException).Should().Be(1);
        (await _db.GetCollection<Authorization>(Collection).CountDocumentsAsync(
            Builders<Authorization>.Filter.Eq(a => a.AuthorizationNumber, "AUTH-UNIQUE"))).Should().Be(1);
    }

    [Fact]
    public async Task AuthorizationNumbers_AreUniqueWithinATenant_ButMayBeReusedByAnotherTenant()
    {
        await Repo().CreateAsync(new Authorization { Id = "own", AuthorizationNumber = "AUTH-SHARED" });
        var context = new DefaultHttpContext();
        context.Items["TenantId"] = "another-tenant";
        var other = new AuthorizationRepositoryMongo(_db, new ConfigurationBuilder().Build(),
            new HttpContextAccessor { HttpContext = context }, NullLogger<AuthorizationRepositoryMongo>.Instance);
        await other.CreateAsync(new Authorization { Id = "other", AuthorizationNumber = "AUTH-SHARED" });
        (await other.GetByAuthorizationNumberAsync("AUTH-SHARED"))!.Id.Should().Be("other");
        (await Repo().GetByAuthorizationNumberAsync("AUTH-SHARED"))!.Id.Should().Be("own");
    }

    [Fact]
    public async Task TwoConcurrentUpdates_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await SeedAsync("A1");
        var a = await ReadAsync("A1");
        var b = await ReadAsync("A1");
        a.ReviewerName = "reviewer A";
        b.ReviewerName = "reviewer B";

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpdateAsync(a)),
            Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is AuthorizationConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        var stored = await ReadAsync("A1");
        stored.ReviewerName.Should().BeOneOf("reviewer A", "reviewer B");
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task AReplaceBasedOnAReadBeforeAnSlaEscalation_Conflicts_InsteadOfUndoingIt()
    {
        await SeedAsync("A1");
        var stale = await ReadAsync("A1");
        var at = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);

        (await Repo().RaiseSlaEscalationAsync(Tenant, "A1", SlaEscalationLevel.Critical, at)).Should().BeTrue();
        stale.ReviewerName = "edited from a stale read";

        var act = () => Repo().UpdateAsync(stale);

        await act.Should().ThrowAsync<AuthorizationConcurrencyException>();
        var stored = await ReadAsync("A1");
        stored.SlaEscalation.Should().Be(SlaEscalationLevel.Critical, "the escalation is not undone");
        stored.ReviewerName.Should().BeNull();
        stored.Revision.Should().Be(1, "the escalation bumped the revision");
    }

    [Fact]
    public async Task ADecisionExtension_MovesTheDeadline_RecordsEvidence_AndSurvivesLaterWrites()
    {
        var seeded = await SeedAsync("X1");
        await _db.GetCollection<BsonDocument>(Collection).UpdateOneAsync(new BsonDocument("_id", "X1"),
            new BsonDocument("$set", new BsonDocument { { "LineOfBusiness", (int)LineOfBusiness.Medicaid } }));
        (await Repo().RaiseSlaEscalationAsync(Tenant, "X1", SlaEscalationLevel.Critical, seeded.SubmittedDate.AddDays(6))).Should().BeTrue();
        var read = await ReadAsync("X1");
        var stale = await ReadAsync("X1");
        var extension = AuthorizationDeadlinePolicy.Extend(read, new ExtendDecisionDeadline
        {
            Basis = DecisionExtensionBasis.ProviderRequested, Reason = "Provider is sending imaging",
        }, "reviewer-1", read.SubmittedDate.AddDays(6));
        var fact = AuthorizationService.Services.Cms0057.PaReportingEvidence.NativeExtension(read, extension, read.Revision + 1);

        await Repo().ExtendDecisionDeadlineAsync(read, extension, fact);

        var stored = await ReadAsync("X1");
        stored.DecisionDueAt.Should().Be(seeded.SubmittedDate.AddDays(21));
        stored.DecisionExtension!.Basis.Should().Be(DecisionExtensionBasis.ProviderRequested);
        stored.SlaEscalation.Should().Be(SlaEscalationLevel.None, "the watchdog re-raises against the new deadline");
        stored.ReportingLedger!.Facts.Should().ContainSingle(f => f.Kind == PaReportingFactKind.ReviewExtension);
        stored.Revision.Should().Be(read.Revision + 1);

        var again = () => Repo().ExtendDecisionDeadlineAsync(stale, extension, fact);
        await again.Should().ThrowAsync<AuthorizationConcurrencyException>("a second extension from a stale read is refused");

        stored.ReviewerName = "reviewer-1";
        await Repo().UpdateAsync(stored);
        var after = await ReadAsync("X1");
        after.DecisionExtension.Should().NotBeNull();
        after.DecisionDueAt.Should().Be(seeded.SubmittedDate.AddDays(21));
        after.ReportingLedger!.Facts.Should().ContainSingle(f => f.Kind == PaReportingFactKind.ReviewExtension);
    }

    [Fact]
    public async Task AnAuthorizationWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        await SeedAsync("A1");
        await _db.GetCollection<BsonDocument>(Collection).UpdateOneAsync(
            new BsonDocument("_id", "A1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync("A1");
        legacy.Revision.Should().Be(0);
        legacy.ReviewerName = "first write after the upgrade";

        await Repo().UpdateAsync(legacy);

        (await ReadAsync("A1")).Revision.Should().Be(1);
        legacy.ReviewerName = "second write from the same, now stale, copy";
        legacy.Revision = 0;
        await ((Func<Task>)(() => Repo().UpdateAsync(legacy))).Should().ThrowAsync<AuthorizationConcurrencyException>();
    }

    [Fact]
    public async Task UpdatingAMissingAuthorization_IsStillANoOp_NotAConflict()
    {
        var ghost = new Authorization { Id = "NOPE", AuthorizationNumber = "AUTH-NOPE" };

        await Repo().UpdateAsync(ghost);

        (await Repo().GetByIdAsync("NOPE")).Should().BeNull();
    }

    [Fact]
    public async Task TheRfaiConsumer_RetriesAfterAConcurrentReviewerWrite_AndBothSurvive()
    {
        await SeedAsync("A1");
        var reviewerWrote = false;
        var repo = InterleavedRepository(async () =>
        {
            if (reviewerWrote) return;
            reviewerWrote = true;
            var reviewer = await ReadAsync("A1");
            reviewer.ReviewerName = "reviewer note";
            await Repo().UpdateAsync(reviewer);
        });
        var consumer = new RfaiDocsReceivedConsumer(repo, NullLogger<RfaiDocsReceivedConsumer>.Instance);
        var receivedAt = new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc);

        await consumer.ProcessMessageAsync(Message("AUTH-A1", receivedAt));

        reviewerWrote.Should().BeTrue();
        var stored = await ReadAsync("A1");
        stored.ReviewerName.Should().Be("reviewer note", "the reviewer's write is not overwritten");
        stored.Status.Should().Be(AuthorizationStatus.InReview, "the consumer re-applied to the fresh copy");
        stored.RFAIResponseDate.Should().Be(receivedAt);
        stored.Revision.Should().Be(2);
    }

    [Fact]
    public async Task TheRfaiConsumer_AfterAConcurrentDecision_HonoursIt_OnTheReread()
    {
        await SeedAsync("A1");
        var reviewerWrote = false;
        var repo = InterleavedRepository(async () =>
        {
            if (reviewerWrote) return;
            reviewerWrote = true;
            var reviewer = await ReadAsync("A1");
            reviewer.Status = AuthorizationStatus.Approved;
            await Repo().UpdateAsync(reviewer);
        });
        var consumer = new RfaiDocsReceivedConsumer(repo, NullLogger<RfaiDocsReceivedConsumer>.Instance);

        await consumer.ProcessMessageAsync(Message("AUTH-A1", DateTime.UtcNow));

        (await ReadAsync("A1")).Status.Should().Be(AuthorizationStatus.Approved, "the decision is not turned back into review");
    }

    /// <summary>
    /// The real repository, with <paramref name="beforeUpdate"/> run between
    /// the consumer's read and its write.
    /// </summary>
    private IAuthorizationRepository InterleavedRepository(Func<Task> beforeUpdate)
    {
        var mock = new Mock<IAuthorizationRepository>();
        mock.Setup(r => r.GetByAuthorizationNumberAsync(It.IsAny<string>()))
            .Returns((string n) => Repo().GetByAuthorizationNumberAsync(n));
        mock.Setup(r => r.UpdateAsync(It.IsAny<Authorization>()))
            .Returns(async (Authorization a) =>
            {
                await beforeUpdate();
                return await Repo().UpdateAsync(a);
            });
        return mock.Object;
    }

    private static RfaiDocsReceivedMessage Message(string authNumber, DateTime receivedAt) => new()
    {
        TenantId = Tenant,
        RfaiCaseId = Guid.NewGuid().ToString(),
        AuthNumber = authNumber,
        ReceivedAt = receivedAt,
        AttachmentIds = new List<string> { "att-1" },
        AllRequestedItemsReceived = true,
    };
}
