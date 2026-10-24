using AuthorizationService.Exceptions;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AuthorizationService.Tests.Persistence;

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class PaReportingMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private static readonly DateTime Receipt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private IMongoDatabase _database = null!;
    public Task InitializeAsync() { _database = mongo.CreateDatabase("pa_reporting"); return Task.CompletedTask; }
    public Task DisposeAsync() => mongo.DropDatabaseAsync(_database);
    private AuthorizationRepositoryMongo Repo(string tenant = "tenant-a")
    {
        var context = new DefaultHttpContext(); context.Items["TenantId"] = tenant;
        return new(_database, new ConfigurationBuilder().Build(), new HttpContextAccessor { HttpContext = context },
            NullLogger<AuthorizationRepositoryMongo>.Instance);
    }
    private Task<Authorization> Seed(string id = "pa-1", string tenant = "tenant-a") => Repo(tenant).CreateAsync(new()
    {
        Id = id, AuthorizationNumber = id, SubmittedDate = Receipt, Status = AuthorizationStatus.Denied,
        ReviewedDate = Receipt.AddDays(1), LineOfBusiness = LineOfBusiness.Medicaid,
        RequestedServices = new() { new() { ProcedureCode = "99213" } }, CreatedBy = "intake",
    });
    private static RecordPaReportingFact Command(string id = "appeal-1") => new()
    {
        EventId = id, SourceSystem = "pa-review", EvidenceReference = "appeal:1/decision:1",
        Kind = PaReportingFactKind.AppealDecision, EffectiveAt = Receipt.AddDays(5), Outcome = AuthorizationStatus.Approved,
    };

    [Fact]
    public async Task LedgerRoundTrips_ExactRetryDoesNotBumpRevision_ConflictingRetryRefused()
    {
        await Seed();
        var fact = await Repo().RecordReportingFactAsync("pa-1", Command(), "actor-a");
        var afterFirst = (await Repo().GetByIdAsync("pa-1"))!;
        afterFirst.ReportingLedger!.Facts.Should().HaveCount(2);
        var replay = await Repo().RecordReportingFactAsync("pa-1", Command(), "actor-b");
        replay!.RecordedAt.Should().Be(fact!.RecordedAt);
        replay.RecordedBy.Should().Be("actor-a");
        (await Repo().GetByIdAsync("pa-1"))!.Revision.Should().Be(afterFirst.Revision);
        var conflict = Command(); conflict.Outcome = AuthorizationStatus.Denied;
        await ((Func<Task>)(() => Repo().RecordReportingFactAsync("pa-1", conflict, "actor")))
            .Should().ThrowAsync<ConcurrencyConflictException>();
    }

    [Fact]
    public async Task CrossTenantAndMissingRecords_DoNotAppendFacts()
    {
        await Seed();
        (await Repo("tenant-b").RecordReportingFactAsync("pa-1", Command(), "actor")).Should().BeNull();
        (await Repo().RecordReportingFactAsync("missing", Command(), "actor")).Should().BeNull();
        (await Repo().GetByIdAsync("pa-1"))!.ReportingLedger!.Facts.Should().ContainSingle();
    }

    [Fact]
    public async Task OrdinaryReplace_CannotEraseFactsOrAlterReportingReceipt_StaleCopyConflicts()
    {
        await Seed();
        var stale = (await Repo().GetByIdAsync("pa-1"))!;
        await Repo().RecordReportingFactAsync("pa-1", Command(), "actor");
        await ((Func<Task>)(() => Repo().UpdateAsync(stale))).Should().ThrowAsync<AuthorizationConcurrencyException>();
        var current = (await Repo().GetByIdAsync("pa-1"))!;
        current.ReportingLedger = new() { ReceivedAt = Receipt.AddYears(1) };
        current.SubmittedDate = Receipt.AddYears(1); current.LineOfBusiness = LineOfBusiness.Commercial;
        await Repo().UpdateAsync(current);
        var stored = (await Repo().GetByIdAsync("pa-1"))!;
        stored.ReportingLedger!.ReceivedAt.Should().Be(Receipt);
        stored.ReportingLedger.Facts.Should().HaveCount(2);
        var cohort = new List<Authorization>();
        await foreach (var a in Repo().StreamReportingCohortAsync(Receipt.Date, Receipt.AddDays(1), LineOfBusiness.Medicaid)) cohort.Add(a);
        cohort.Should().ContainSingle().Which.Id.Should().Be("pa-1");
    }

    [Fact]
    public async Task ConcurrentDuplicateDelivery_StoresExactlyOneFact()
    {
        await Seed();
        async Task<Exception?> Attempt()
        {
            try { await Repo().RecordReportingFactAsync("pa-1", Command(), "actor"); return null; }
            catch (AuthorizationConcurrencyException ex) { return ex; }
        }
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Attempt()));
        attempts.Should().Contain(e => e == null);
        (await Repo().GetByIdAsync("pa-1"))!.ReportingLedger!.Facts
            .Count(f => f.EventId == "appeal-1").Should().Be(1);
    }

    [Fact]
    public async Task LegacyRecord_IsDiagnosed_AndCannotAcquireFabricatedHistory()
    {
        await Seed(); await Seed("other", "tenant-b");
        await _database.GetCollection<BsonDocument>("Authorizations").UpdateOneAsync(
            new BsonDocument("_id", "pa-1"), new BsonDocument("$unset", new BsonDocument("ReportingLedger", "")));
        var current = (await Repo().GetByIdAsync("pa-1"))!;
        await Repo().UpdateAsync(current);
        (await Repo().GetByIdAsync("pa-1"))!.ReportingLedger.Should().BeNull();
        await ((Func<Task>)(() => Repo().RecordReportingFactAsync("pa-1", Command(), "actor")))
            .Should().ThrowAsync<ConcurrencyConflictException>();
        var cohort = new List<Authorization>();
        await foreach (var a in Repo().StreamReportingCohortAsync(Receipt.Date, Receipt.AddDays(1), LineOfBusiness.Medicaid)) cohort.Add(a);
        cohort.Should().ContainSingle().Which.TenantId.Should().Be("tenant-a");
    }
}
