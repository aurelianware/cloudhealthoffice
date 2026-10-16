using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB billing run repository (EphemeralMongo).
/// <c>UpdateAsync</c> replaced the run unconditionally, so two executes that
/// both read it Pending both moved it to Running and both generated invoices,
/// and a stale replace undid whatever was written in between. Every write now
/// bumps <see cref="BillingRun.Revision"/> and a replace is conditional on it.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class BillingRunRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-run-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("pb_billing_run_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    /// <summary>One request's context (not <see cref="HttpContextAccessor"/>, whose AsyncLocal is shared across concurrent flows).</summary>
    private sealed class RequestContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static IHttpContextAccessor Request()
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        return new RequestContext { HttpContext = http };
    }

    private BillingRunRepositoryMongo Repo() => new(_db, Request(), NullLogger<BillingRunRepositoryMongo>.Instance);

    private async Task<BillingRun> SeedAsync() => await Repo().CreateAsync(new BillingRun
    {
        BillingRunNumber = "BR-2026-10-TEST",
        BillingPeriod = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        Status = BillingRunStatus.Pending,
        CreatedBy = "scheduler",
    });

    private async Task<BillingRun> ReadAsync(string id) => (await Repo().GetByIdAsync(id))!;

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
        var run = await SeedAsync();
        var a = await ReadAsync(run.Id);
        var b = await ReadAsync(run.Id);
        a.Description = "writer A";
        b.Description = "writer B";

        var outcomes = await Task.WhenAll(Try(() => Repo().UpdateAsync(a)), Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is BillingRunConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        outcomes.OfType<BillingRunConcurrencyException>().Single()
            .Should().BeAssignableTo<CloudHealthOffice.Infrastructure.Data.ConcurrencyConflictException>("the shared middleware answers it with 409");
        var stored = await ReadAsync(run.Id);
        stored.Description.Should().BeOneOf("writer A", "writer B");
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task AReplaceBasedOnAReadBeforeAnotherWrite_Conflicts_InsteadOfUndoingIt()
    {
        var run = await SeedAsync();
        var stale = await ReadAsync(run.Id);

        var started = await ReadAsync(run.Id);
        started.Status = BillingRunStatus.Running;
        await Repo().UpdateAsync(started);

        stale.Status = BillingRunStatus.Cancelled;
        var act = () => Repo().UpdateAsync(stale);

        await act.Should().ThrowAsync<BillingRunConcurrencyException>();
        stale.Revision.Should().Be(0, "a failed write leaves the copy at the revision it was read at");
        (await ReadAsync(run.Id)).Status.Should().Be(BillingRunStatus.Running, "the start is not undone by the stale cancel");
    }

    [Fact]
    public async Task ARunWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        var run = await SeedAsync();
        await _db.GetCollection<BsonDocument>("BillingRuns").UpdateOneAsync(
            new BsonDocument("_id", run.Id), new BsonDocument("$unset", new BsonDocument(nameof(BillingRun.Revision), "")));
        var legacy = await ReadAsync(run.Id);
        var legacyAgain = await ReadAsync(run.Id);
        legacy.Revision.Should().Be(0);
        legacy.Description = "first write after the upgrade";

        await Repo().UpdateAsync(legacy);

        (await ReadAsync(run.Id)).Revision.Should().Be(1);
        legacyAgain.Description = "second write from the same legacy read";
        await ((Func<Task>)(() => Repo().UpdateAsync(legacyAgain))).Should().ThrowAsync<BillingRunConcurrencyException>();
        (await ReadAsync(run.Id)).Description.Should().Be("first write after the upgrade");
    }

    [Fact]
    public async Task UpdatingARunThatDoesNotExist_WritesNothing_AndDoesNotThrow()
    {
        var ghost = new BillingRun { TenantId = Tenant, BillingRunNumber = "BR-GHOST" };

        await Repo().UpdateAsync(ghost);

        (await Repo().GetByIdAsync(ghost.Id)).Should().BeNull();
    }

    // --- PremiumBillingService over the real repository ---

    /// <summary>Holds every executor's first read until all of them have read the run, so they race from the same Pending copy.</summary>
    private sealed class SameReadRuns(IBillingRunRepository inner, TaskCompletionSource allRead, Func<bool> lastToRead) : IBillingRunRepository
    {
        private bool _held;

        public async Task<BillingRun?> GetByIdAsync(string id)
        {
            var run = await inner.GetByIdAsync(id);
            if (_held)
                return run;
            _held = true;
            if (lastToRead())
                allRead.TrySetResult();
            await allRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return run;
        }

        public Task<BillingRun?> GetByBillingRunNumberAsync(string billingRunNumber) => inner.GetByBillingRunNumberAsync(billingRunNumber);
        public Task<IEnumerable<BillingRun>> SearchAsync(DateTime? from, DateTime? to, BillingRunStatus? status = null) => inner.SearchAsync(from, to, status);
        public Task<BillingRun> CreateAsync(BillingRun billingRun) => inner.CreateAsync(billingRun);
        public Task<BillingRun> UpdateAsync(BillingRun billingRun) => inner.UpdateAsync(billingRun);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
    }

    private static Mock<ICurrentActor> Actor(string userId)
    {
        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.UserId).Returns(userId);
        actor.SetupGet(a => a.TenantId).Returns(Tenant);
        actor.SetupGet(a => a.IsAuthenticated).Returns(true);
        return actor;
    }

    private static PremiumBillingService.Services.PremiumBillingService Billing(IBillingRunRepository runs, ISponsorServiceClient sponsors, string userId)
        => new(runs, Mock.Of<IPremiumInvoiceRepository>(), sponsors, Mock.Of<ICoverageServiceClient>(), Actor(userId).Object,
            Mock.Of<ILogger<PremiumBillingService.Services.PremiumBillingService>>());

    [Fact]
    public async Task TwoConcurrentExecutes_OfOnePendingRun_OnlyOneRuns()
    {
        var run = await SeedAsync();
        var sponsorFetches = 0;
        var sponsors = new Mock<ISponsorServiceClient>();
        sponsors.Setup(s => s.GetActiveSponsorsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref sponsorFetches);
                return new List<SponsorDto>();
            });
        var allRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        bool LastToRead() => Interlocked.Increment(ref reads) == 2;

        var outcomes = await Task.WhenAll(
            Try(() => Task.Run(() => Billing(new SameReadRuns(Repo(), allRead, LastToRead), sponsors.Object, "user-1").ExecuteBillingRunAsync(run.Id))),
            Try(() => Task.Run(() => Billing(new SameReadRuns(Repo(), allRead, LastToRead), sponsors.Object, "user-2").ExecuteBillingRunAsync(run.Id))));

        outcomes.Count(o => o is null).Should().Be(1, "one execute runs the billing");
        outcomes.Count(o => o is BillingRunConcurrencyException).Should().Be(1, "the other loses the Pending to Running write (409) and does not proceed");
        sponsorFetches.Should().Be(1, "only the winner generates invoices");
        var stored = await ReadAsync(run.Id);
        stored.Status.Should().Be(BillingRunStatus.Completed);
        stored.Revision.Should().Be(2, "Running, then Completed: the loser wrote nothing");
    }

    [Fact]
    public async Task ARunsOutcome_KeepsWarningsWrittenToItWhileItRan()
    {
        var run = await SeedAsync();
        var sponsors = new Mock<ISponsorServiceClient>();
        sponsors.Setup(s => s.GetActiveSponsorsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // A batch EFT records a warning on the run while it is Running.
                var running = await ReadAsync(run.Id);
                running.Warnings.Add("EFT needs attention for invoice inv-1");
                await Repo().UpdateAsync(running);
                return new List<SponsorDto>();
            });

        var result = await Billing(Repo(), sponsors.Object, "user-1").ExecuteBillingRunAsync(run.Id);

        result.Status.Should().Be(BillingRunStatus.Completed);
        var stored = await ReadAsync(run.Id);
        stored.Status.Should().Be(BillingRunStatus.Completed, "the outcome is re-applied over the concurrent write");
        stored.Warnings.Should().Contain("EFT needs attention for invoice inv-1", "the concurrent write is not undone");
        stored.Revision.Should().Be(3);
    }
}
