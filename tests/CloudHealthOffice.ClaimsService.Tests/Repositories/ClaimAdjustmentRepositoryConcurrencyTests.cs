using ClaimsService.Exceptions;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB claim adjustment repository
/// (EphemeralMongo). <c>UpdateAsync</c> replaced the row filtered on Tenant +
/// Id only, so step 11 of <c>CreateAdjustmentAsync</c> (which sets NewClaimId
/// on its in-memory copy while the pipeline runs asynchronously) could write
/// Status = AwaitingReadjudication over a PendingReversal / Failed transition
/// another writer had already made, and a late duplicate callback could undo
/// an Active transition. Updates are now conditional on
/// <see cref="ClaimAdjustment.Revision"/>; the service re-reads and re-applies
/// only its own change.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ClaimAdjustmentRepositoryConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("claim_adjustment_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private ClaimAdjustmentRepositoryMongo Repo() => new(_db, new ConfigurationBuilder().Build());

    private static ClaimAdjustment Row(string id = "adj-1", ClaimAdjustmentStatus status = ClaimAdjustmentStatus.AwaitingReadjudication) => new()
    {
        Id = id, TenantId = Tenant, ClaimVersionId = "chain-" + id, PredecessorClaimId = "pred-" + id,
        PredecessorVersionId = "pred-" + id, NewClaimId = "new-" + id, AdjustmentReason = "correction",
        IdempotencyKey = "idem-" + id, RequestHash = "hash", CreatedBy = "actor", Status = status,
    };

    private async Task<ClaimAdjustment> ReadAsync(string id = "adj-1") => (await Repo().GetByIdAsync(Tenant, id))!;

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
    public async Task Two_concurrent_updates_from_the_same_read_one_wins_the_other_conflicts()
    {
        await Repo().CreateAsync(Row());
        var a = await ReadAsync();
        var b = await ReadAsync();
        a.Status = ClaimAdjustmentStatus.PendingReversal;
        b.Status = ClaimAdjustmentStatus.Failed;

        var outcomes = await Task.WhenAll(Try(() => Repo().UpdateAsync(a)), Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1);
        outcomes.OfType<ClaimAdjustmentConcurrencyException>().Should().ContainSingle()
            .Which.Should().BeAssignableTo<ConcurrencyConflictException>();
        (await ReadAsync()).Revision.Should().Be(1);
    }

    [Fact]
    public async Task A_row_written_before_the_revision_existed_updates_once_then_carries_one()
    {
        await Repo().CreateAsync(Row());
        await _db.GetCollection<BsonDocument>("ClaimAdjustments").UpdateOneAsync(
            new BsonDocument("_id", "adj-1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync();
        var legacyToo = await ReadAsync();
        legacy.Revision.Should().Be(0);
        legacy.Status = ClaimAdjustmentStatus.PendingReversal;

        await Repo().UpdateAsync(legacy);

        (await ReadAsync()).Revision.Should().Be(1);
        legacyToo.NewClaimId = "stale";
        await ((Func<Task>)(() => Repo().UpdateAsync(legacyToo))).Should().ThrowAsync<ClaimAdjustmentConcurrencyException>();
        legacyToo.Revision.Should().Be(0, "the in-memory revision is restored when nothing was written");
    }

    [Fact]
    public async Task Updating_a_missing_row_is_still_not_found_not_a_conflict()
    {
        var act = () => Repo().UpdateAsync(Row("ghost"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found for update*");
    }

    [Fact]
    public async Task A_new_row_starts_at_revision_zero()
    {
        var row = Row();
        row.Revision = 9;

        await Repo().CreateAsync(row);

        (await ReadAsync()).Revision.Should().Be(0);
    }

    // ---- service callers ----

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IClaimSubmissionService _submission = Substitute.For<IClaimSubmissionService>();

    private ClaimAdjustmentService Service(IClaimAdjustmentRepository repo) => new(
        _claims, repo, _submission, Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IMessageBus>(),
        NullLogger<ClaimAdjustmentService>.Instance);

    [Fact]
    public async Task A_stale_step_11_write_cannot_erase_a_PendingReversal_set_meanwhile()
    {
        var predecessor = new Claim
        {
            Id = "pred-1", TenantId = Tenant, ClaimVersionId = "pred-1", VersionNumber = 1,
            VersionState = ClaimVersionState.Paid, Status = ClaimStatus.Paid, ClaimNumber = "CLM-1",
            MemberId = "m1", BillingProviderNPI = "1234567890",
        };
        _claims.GetByIdAsync("pred-1").Returns(predecessor);
        _claims.MarkSupersededProjectionAsync(Tenant, "pred-1", Arg.Any<string>(), Arg.Any<DateTime>(), "actor", Arg.Any<CancellationToken>())
            .Returns(true);
        _submission
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                // While the new version's pipeline runs, another writer moves
                // the placeholder row on (PendingReversal); step 11 still holds
                // its AwaitingReadjudication copy from step 5.
                var row = (await Repo().GetByClaimVersionIdAsync(Tenant, "pred-1"))!;
                row.Status = ClaimAdjustmentStatus.PendingReversal;
                row.ReadjudicationCompletedAt = DateTime.UtcNow;
                await Repo().UpdateAsync(row);
                var c = ci.Arg<AdapterClaim>();
                c.Id = "new-1";
                c.ClaimVersionId = "pred-1";
                return ClaimSubmissionResult.Ok(c);
            });

        var result = await Service(Repo()).CreateAdjustmentAsync(
            "pred-1", new ClaimAdjustmentRequest { AdjustmentReason = "fix", CorrectedClaim = new AdapterClaim { MemberId = "m1" } },
            "idem-1", Tenant, "actor", "corr-1");

        result.Outcome.Should().Be(ClaimAdjustmentOutcome.Created);
        var stored = (await Repo().GetByClaimVersionIdAsync(Tenant, "pred-1"))!;
        stored.Status.Should().Be(ClaimAdjustmentStatus.PendingReversal, "step 11 re-applies only NewClaimId");
        stored.ReadjudicationCompletedAt.Should().NotBeNull();
        stored.NewClaimId.Should().Be("new-1");
        result.Adjustment!.Status.Should().Be(ClaimAdjustmentStatus.PendingReversal);
    }

    [Fact]
    public async Task A_late_duplicate_finalize_callback_cannot_undo_an_Active_transition()
    {
        await Repo().CreateAsync(Row());
        var repo = new InterleavingRepo(Repo(), async () =>
        {
            // Between the duplicate callback's read and its write, the first
            // callback and the reversal completion run.
            var row = await ReadAsync();
            row.Status = ClaimAdjustmentStatus.Active;
            row.ReversalRunId = "run-1";
            await Repo().UpdateAsync(row);
        });

        await Service(repo).OnNewVersionFinalizedAsync(Tenant, "new-adj-1", ClaimAdjudicationOutcome.Pass);

        var stored = await ReadAsync();
        stored.Status.Should().Be(ClaimAdjustmentStatus.Active);
        stored.ReversalRunId.Should().Be("run-1");
    }

    [Fact]
    public async Task MarkActive_overtaken_by_an_unrelated_write_rereads_and_still_activates()
    {
        await Repo().CreateAsync(Row(status: ClaimAdjustmentStatus.PendingReversal));
        var repo = new InterleavingRepo(Repo(), async () =>
        {
            var row = await ReadAsync();
            row.Notes = "operator note";
            await Repo().UpdateAsync(row);
        });

        await Service(repo).MarkActiveOnReversalAsync(Tenant, "pred-adj-1", "run-1");

        var stored = await ReadAsync();
        stored.Status.Should().Be(ClaimAdjustmentStatus.Active);
        stored.ReversalRunId.Should().Be("run-1");
        stored.Notes.Should().Be("operator note");
    }

    /// <summary>Runs a hook once, right after the callback lookup: the copy it returned is then stale.</summary>
    private sealed class InterleavingRepo(IClaimAdjustmentRepository inner, Func<Task> afterLookup) : IClaimAdjustmentRepository
    {
        private Func<Task>? _hook = afterLookup;

        private async Task<ClaimAdjustment?> Stale(Task<ClaimAdjustment?> read)
        {
            var row = await read;
            var hook = _hook;
            _hook = null;
            if (hook != null) await hook();
            return row;
        }

        public Task<ClaimAdjustment?> GetByNewClaimIdAsync(string tenantId, string newClaimId, CancellationToken ct = default) =>
            Stale(inner.GetByNewClaimIdAsync(tenantId, newClaimId, ct));
        public Task<ClaimAdjustment?> GetByPredecessorAndStatusAsync(string tenantId, string predecessorClaimId, ClaimAdjustmentStatus status, CancellationToken ct = default) =>
            Stale(inner.GetByPredecessorAndStatusAsync(tenantId, predecessorClaimId, status, ct));
        public Task<ClaimAdjustment> CreateAsync(ClaimAdjustment adjustment, CancellationToken ct = default) => inner.CreateAsync(adjustment, ct);
        public Task<ClaimAdjustment?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default) => inner.GetByIdAsync(tenantId, id, ct);
        public Task<ClaimAdjustment?> GetByClaimVersionIdAsync(string tenantId, string claimVersionId, CancellationToken ct = default) => inner.GetByClaimVersionIdAsync(tenantId, claimVersionId, ct);
        public Task<ClaimAdjustment?> GetByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken ct = default) => inner.GetByIdempotencyKeyAsync(tenantId, idempotencyKey, ct);
        public Task<(IReadOnlyList<ClaimAdjustment> Page, int TotalCount)> ListAsync(string tenantId, ClaimAdjustmentListFilter filter, CancellationToken ct = default) => inner.ListAsync(tenantId, filter, ct);
        public Task<ClaimAdjustment> UpdateAsync(ClaimAdjustment adjustment, CancellationToken ct = default) => inner.UpdateAsync(adjustment, ct);
        public Task<bool> DeleteAsync(string tenantId, string id, CancellationToken ct = default) => inner.DeleteAsync(tenantId, id, ct);
    }
}
