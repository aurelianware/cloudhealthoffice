using CloudHealthOffice.Testing.Mongo;
using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer.Outbound.Bulk;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace CloudHealthOffice.FhirService.Tests.PayerToPayer;

/// <summary>The durable store of outbound bulk Payer-to-Payer jobs on a real mongod (G-35).</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoPayerToPayerBulkOutboundJobStoreTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MongoPayerToPayerBulkOutboundJobStore _store = null!;
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public MongoPayerToPayerBulkOutboundJobStoreTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("p2p_outbound_bulk_jobs");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MongoDb:DatabaseName"] = _database.DatabaseNamespace.DatabaseName })
            .Build();
        _store = new MongoPayerToPayerBulkOutboundJobStore(_mongo.Client, configuration);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private static PayerToPayerBulkOutboundJob Job(string id, string tenant = "t") => new()
    {
        JobId = id,
        TenantId = tenant,
        TargetPayerId = "PRIOR-PLAN",
        Status = PayerToPayerBulkOutboundStatus.Matching,
        StatusUrl = "https://prior-payer.test/status/1",
        Members =
        [
            new() { MemberId = "m-1", CorrelationId = "c-1", Outcome = PayerToPayerBulkMemberOutcome.Submitted },
        ],
        CreatedAt = Now,
        UpdatedAt = Now,
        NextAttemptAt = Now,
        ExpiresAt = Now.AddDays(31),
    };

    [Fact]
    public async Task AJob_RoundTrips_InItsTenantOnly()
    {
        await _store.SaveAsync(Job("j-1"));

        (await _store.GetAsync("t", "j-1", Now)).Should().BeEquivalentTo(Job("j-1"));
        (await _store.GetAsync("t2", "j-1", Now)).Should().BeNull();
        (await _store.GetAsync("t", "j-1", Now.AddDays(32))).Should().BeNull("an expired job is never answered");
    }

    [Fact]
    public async Task ADueJob_IsClaimedOnce_UntilItsLeaseRunsOut_AndAFinishedJobNever()
    {
        await _store.SaveAsync(Job("j-1"));
        var done = Job("j-2");
        done.Status = PayerToPayerBulkOutboundStatus.Completed;
        await _store.SaveAsync(done);

        (await _store.ClaimDueAsync(Now, Now.AddMinutes(10), 5)).Select(j => j.JobId).Should().Equal("j-1");
        (await _store.ClaimDueAsync(Now.AddMinutes(1), Now.AddMinutes(11), 5)).Should().BeEmpty();
        (await _store.ClaimDueAsync(Now.AddMinutes(11), Now.AddMinutes(21), 5)).Select(j => j.JobId).Should().Equal("j-1");
    }

    [Fact]
    public async Task AJobNotYetDue_IsNotClaimed()
    {
        var later = Job("j-1");
        later.NextAttemptAt = Now.AddMinutes(5);
        await _store.SaveAsync(later);

        (await _store.ClaimDueAsync(Now, Now.AddMinutes(10), 5)).Should().BeEmpty();
        (await _store.ClaimDueAsync(Now.AddMinutes(5), Now.AddMinutes(15), 5)).Should().HaveCount(1);
    }
}
