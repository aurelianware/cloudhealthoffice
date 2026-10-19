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
