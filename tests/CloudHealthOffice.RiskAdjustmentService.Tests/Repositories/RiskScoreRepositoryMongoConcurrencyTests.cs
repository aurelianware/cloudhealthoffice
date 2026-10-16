using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using RiskAdjustmentService.Controllers;
using RiskAdjustmentService.Models;
using RiskAdjustmentService.Repositories;

namespace CloudHealthOffice.RiskAdjustmentService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB risk score repository, against a real
/// mongod. <c>UpdateAsync</c> replaced the member's RAF score unconditionally,
/// so of two concurrent writes the later silently erased the earlier: a
/// recalculation read before a batch status change to Submitted turned the
/// score back to Calculated and cleared <c>IsSubmitted</c>. The replace is now
/// conditional on the revision it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class RiskScoreRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-raf-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("raf_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    /// <summary>One request's context (not the AsyncLocal-backed HttpContextAccessor: these tests run requests at once).</summary>
    private sealed class RequestContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class Actor : ICurrentActor
    {
        public bool IsAuthenticated => true;
        public string UserId => "token-user";
        public string? DisplayName => null;
        public string? Email => null;
        public string TenantId => Tenant;
        public bool IsService => false;
        public IReadOnlyCollection<string> Roles => [];
        public bool HasPermission(string permission) => true;
    }

    private static IHttpContextAccessor Request()
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        return new RequestContext { HttpContext = http };
    }

    private RiskScoreRepositoryMongo Repo() => new(_db, Request(), NullLogger<RiskScoreRepositoryMongo>.Instance);

    private Task<MemberRiskScore> SeedAsync(string id) => Repo().CreateAsync(new MemberRiskScore
    {
        Id = id,
        MemberId = "M1",
        MeasurementYear = 2026,
        RiskModel = "CMS-HCC",
        LineOfBusiness = LineOfBusiness.Medicare,
        DemographicFactor = 0.4m,
        HccFactor = 0.6m,
        RiskScore = 1.0m,
        Status = ScoreStatus.Calculated,
    });

    private async Task<MemberRiskScore> ReadAsync(string id) => (await Repo().GetByIdAsync(id))!;

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
    public async Task TwoConcurrentScoreWrites_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await SeedAsync("rs-1");
        var a = await ReadAsync("rs-1");
        var b = await ReadAsync("rs-1");
        a.RiskScore = 1.25m;
        b.RiskScore = 0.85m;

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpdateAsync(a)),
            Try(() => Repo().UpdateAsync(b)));

        Assert.Equal(1, outcomes.Count(o => o is null));
        var conflict = Assert.Single(outcomes.OfType<RiskScoreConcurrencyException>());
        Assert.IsAssignableFrom<ConcurrencyConflictException>(conflict);
        var stored = await ReadAsync("rs-1");
        Assert.Contains(stored.RiskScore, new[] { 1.25m, 0.85m });
        Assert.Equal(1, stored.Revision);
    }

    [Fact]
    public async Task ARecalculationReadBeforeTheScoreWasSubmitted_Conflicts_InsteadOfUnsubmittingIt()
    {
        await SeedAsync("rs-1");
        var recalculation = await ReadAsync("rs-1");
        var submit = await ReadAsync("rs-1");
        submit.Status = ScoreStatus.Submitted;
        submit.IsSubmitted = true;
        submit.SubmittedDate = DateTime.UtcNow;
        await Repo().UpdateAsync(submit);

        recalculation.RiskScore = 1.4m;
        recalculation.Status = ScoreStatus.Calculated;

        await Assert.ThrowsAsync<RiskScoreConcurrencyException>(() => Repo().UpdateAsync(recalculation));
        Assert.Equal(0, recalculation.Revision);
        var stored = await ReadAsync("rs-1");
        Assert.Equal(ScoreStatus.Submitted, stored.Status);
        Assert.True(stored.IsSubmitted);
        Assert.Equal(1.0m, stored.RiskScore);
    }

    [Fact]
    public async Task AScoreWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        await SeedAsync("rs-1");
        await _db.GetCollection<BsonDocument>("riskScores").UpdateOneAsync(
            new BsonDocument("_id", "rs-1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync("rs-1");
        Assert.Equal(0, legacy.Revision);
        legacy.RiskScore = 1.1m;

        await Repo().UpdateAsync(legacy);

        var stored = await ReadAsync("rs-1");
        Assert.Equal(1, stored.Revision);
        Assert.Equal(1.1m, stored.RiskScore);
    }

    [Fact]
    public async Task UpdatingAMissingScore_IsStillANoOp_NotAConflict()
    {
        var ghost = new MemberRiskScore { Id = "NOPE", MemberId = "M1", MeasurementYear = 2026 };

        await Repo().UpdateAsync(ghost);

        Assert.Null(await Repo().GetByIdAsync("NOPE"));
        Assert.Equal(0, ghost.Revision);
    }

    [Fact]
    public async Task APutWhoseBodyHasNoRevision_StillUpdatesAScoreThatWasAlreadyChanged()
    {
        await SeedAsync("rs-1");
        var first = await ReadAsync("rs-1");
        first.Status = ScoreStatus.UnderReview;
        await Repo().UpdateAsync(first);

        // The engine is not used on the upsert path.
        var controller = new RiskAdjustmentController(Repo(), null!, new Actor(), NullLogger<RiskAdjustmentController>.Instance);
        var body = new MemberRiskScore
        {
            MemberId = "M1",
            MeasurementYear = 2026,
            RiskModel = "CMS-HCC",
            DemographicFactor = 0.5m,
            RiskScore = 1.2m,
        };

        var result = await controller.UpsertMemberScore("M1", 2026, body);

        Assert.IsType<OkObjectResult>(result.Result);
        var stored = await ReadAsync("rs-1");
        Assert.Equal(1.2m, stored.RiskScore);
        Assert.Equal(2, stored.Revision);
    }
}
