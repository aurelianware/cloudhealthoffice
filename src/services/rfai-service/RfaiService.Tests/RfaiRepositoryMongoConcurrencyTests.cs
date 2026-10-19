using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using RfaiService.Exceptions;
using RfaiService.Models;
using RfaiService.Repositories;
using RfaiService.Services;

namespace RfaiService.Tests;

/// <summary>
/// Optimistic concurrency on the MongoDB RFAI repository (EphemeralMongo).
/// <c>UpdateAsync</c> replaced the case unconditionally, so a delivery
/// callback that read the case before a response intake wrote it back without
/// the recorded artifacts. Every write now bumps <see cref="RfaiCase.Revision"/>
/// and a replace is conditional on the revision it read; the delivery and
/// intake paths re-read and re-apply on a conflict, request paths get a 409.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class RfaiRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("rfai_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private RfaiRepositoryMongo Repo() => new(_db, NullLogger<RfaiRepositoryMongo>.Instance);

    private static RfaiCaseService Service(IRfaiRepository repository) => new(
        repository,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
        NullLogger<RfaiCaseService>.Instance);

    private async Task<RfaiCase> SeedAsync() =>
        (await Service(Repo()).EnsureRequestAsync(new RfaiCreationRequest
        {
            TenantId = Tenant,
            AuthNumber = "PAS-20260906-ABCD1234",
            CorrelationKey = "decision-" + Guid.NewGuid().ToString("N"),
            ReviewDecision = "A4",
            RequestSource = RfaiRequestSources.ReviewDecisionA4,
            RequestedItems = [new RequestedItem { Code = "AS", Description = "Discharge summary" }],
        })).Case;

    private static RfaiResponseArtifact Artifact(string submissionId) => new()
    {
        SubmissionId = submissionId,
        ContentType = "application/pdf",
        SizeBytes = 512,
        Channel = RfaiResponseChannels.CdexSubmitAttachment,
    };

    private async Task<RfaiCase> ReadAsync(string id) => (await Repo().GetByIdAsync(Tenant, id))!;

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
        var seeded = await SeedAsync();
        var a = await ReadAsync(seeded.Id);
        var b = await ReadAsync(seeded.Id);
        RfaiCaseLifecycle.Close(a, "reviewer-a", "done", DateTime.UtcNow);
        RfaiCaseLifecycle.Cancel(b, "reviewer-b", "withdrawn", DateTime.UtcNow);

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpdateAsync(a)),
            Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is RfaiConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        var stored = await ReadAsync(seeded.Id);
        stored.Status.Should().BeOneOf(RfaiStatus.Closed, RfaiStatus.Cancelled);
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task ACloseFromAReadBeforeAResponse_Conflicts_InsteadOfDroppingTheArtifacts()
    {
        var seeded = await SeedAsync();
        var stale = await ReadAsync(seeded.Id);
        await Service(Repo()).RecordResponseAsync(Tenant, seeded.Id, [Artifact("sub-1")]);
        RfaiCaseLifecycle.Close(stale, "reviewer-1", "done", DateTime.UtcNow);

        var act = () => Repo().UpdateAsync(stale);

        await act.Should().ThrowAsync<RfaiConcurrencyException>();
        var stored = await ReadAsync(seeded.Id);
        stored.ReceivedAttachments.Select(a => a.SubmissionId).Should().Equal("sub-1");
        stored.Status.Should().Be(RfaiStatus.DocsReceived);
    }

    [Fact]
    public async Task ACaseWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        var seeded = await SeedAsync();
        await _db.GetCollection<BsonDocument>("RfaiCases").UpdateOneAsync(
            new BsonDocument("_id", seeded.Id), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync(seeded.Id);
        legacy.Revision.Should().Be(0);
        RfaiCaseLifecycle.MarkDelivered(legacy, DateTime.UtcNow);

        await Repo().UpdateAsync(legacy);

        (await ReadAsync(seeded.Id)).Revision.Should().Be(1);
    }

    [Fact]
    public async Task UpdatingAMissingCase_IsStillANoOp_NotAConflict()
    {
        var ghost = new RfaiCase { Id = "NOPE", TenantId = Tenant };

        await Repo().UpdateAsync(ghost);

        (await Repo().GetByIdAsync(Tenant, "NOPE")).Should().BeNull();
    }

    [Fact]
    public async Task ADeliveryCallbackInterleavedWithAResponse_Retries_AndBothSurvive()
    {
        var seeded = await SeedAsync();
        var repo = new InterleavedRepository(Repo(),
            () => Service(Repo()).RecordResponseAsync(Tenant, seeded.Id, [Artifact("sub-1")]));

        var delivered = await Service(repo).MarkDeliveredAsync(Tenant, seeded.Id);

        repo.Interleaved.Should().BeTrue();
        delivered!.DeliveryCount.Should().Be(1);
        var stored = await ReadAsync(seeded.Id);
        stored.DeliveryCount.Should().Be(1, "the delivery is recorded on the re-read");
        stored.ReceivedAttachments.Select(a => a.SubmissionId).Should().Equal(new[] { "sub-1" }, "the response is not dropped");
        stored.Status.Should().Be(RfaiStatus.DocsReceived);
    }

    [Fact]
    public async Task AResponseInterleavedWithADeliveryCallback_Retries_AndBothSurvive()
    {
        var seeded = await SeedAsync();
        var repo = new InterleavedRepository(Repo(),
            () => Service(Repo()).MarkDeliveredAsync(Tenant, seeded.Id));

        var result = await Service(repo).RecordResponseAsync(Tenant, seeded.Id, [Artifact("sub-1")]);

        repo.Interleaved.Should().BeTrue();
        result!.TransitionedToDocsReceived.Should().BeTrue();
        var stored = await ReadAsync(seeded.Id);
        stored.DeliveryCount.Should().Be(1, "the delivery is not dropped");
        stored.ReceivedAttachments.Select(a => a.SubmissionId).Should().Equal("sub-1");
    }

    [Fact]
    public async Task ConcurrentDeliveriesAndResponses_AllSurvive()
    {
        var seeded = await SeedAsync();

        await Task.WhenAll(
            Service(Repo()).MarkDeliveredAsync(Tenant, seeded.Id),
            Service(Repo()).RecordResponseAsync(Tenant, seeded.Id, [Artifact("sub-1")]),
            Service(Repo()).MarkDeliveredAsync(Tenant, seeded.Id),
            Service(Repo()).RecordResponseAsync(Tenant, seeded.Id, [Artifact("sub-2")]));

        var stored = await ReadAsync(seeded.Id);
        stored.DeliveryCount.Should().Be(2);
        stored.ReceivedAttachments.Select(a => a.SubmissionId).Should().BeEquivalentTo("sub-1", "sub-2");
    }

    /// <summary>
    /// The real repository, with one other write run between the first
    /// <c>UpdateAsync</c> caller's read and its write.
    /// </summary>
    private sealed class InterleavedRepository(IRfaiRepository inner, Func<Task> interleave) : IRfaiRepository
    {
        public bool Interleaved { get; private set; }

        public Task<RfaiCase?> GetByIdAsync(string tenantId, string id) => inner.GetByIdAsync(tenantId, id);
        public Task<List<RfaiCase>> GetByAuthNumberAsync(string tenantId, string authNumber) => inner.GetByAuthNumberAsync(tenantId, authNumber);
        public Task<RfaiCase?> GetByTrackingIdAsync(string tenantId, string trackingId) => inner.GetByTrackingIdAsync(tenantId, trackingId);
        public Task<RfaiCase> CreateAsync(RfaiCase rfaiCase) => inner.CreateAsync(rfaiCase);
        public Task<(RfaiCase Case, bool Created)> CreateIfAbsentAsync(RfaiCase rfaiCase) => inner.CreateIfAbsentAsync(rfaiCase);

        public async Task<RfaiCase> UpdateAsync(RfaiCase rfaiCase)
        {
            if (!Interleaved)
            {
                Interleaved = true;
                await interleave();
            }

            return await inner.UpdateAsync(rfaiCase);
        }
    }
}
