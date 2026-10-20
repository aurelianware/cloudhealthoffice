using CloudHealthOffice.Testing.Mongo;
using FhirService.Services.PayerToPayer;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace CloudHealthOffice.FhirService.Tests.PayerToPayer;

/// <summary>The durable Payer-to-Payer consent store on a real mongod (ADR 022).</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoPayerToPayerConsentAttestationStoreTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MongoPayerToPayerConsentAttestationStore _store = null!;
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    public MongoPayerToPayerConsentAttestationStoreTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("p2p_consent");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MongoDb:DatabaseName"] = _database.DatabaseNamespace.DatabaseName })
            .Build();
        _store = new MongoPayerToPayerConsentAttestationStore(_mongo.Client, configuration);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private static PayerToPayerConsentAttestation Attestation(
        string payer = "NEW-PLAN", string member = "m-1", string tenant = "t", DateTime? end = null, string fp = "fp-1") => new()
    {
        TenantId = tenant, MemberId = member, ReceivingPayerId = payer, Policy = PdexConsentValidator.SensitivePolicy,
        PeriodStartUtc = Now.AddDays(-1), PeriodEndUtc = end, ConsentFingerprint = fp,
        RecordedAtUtc = Now, RecordedBy = "client:new-plan",
    };

    [Fact]
    public async Task A_recorded_consent_reads_back_for_its_payer_member_and_tenant_only()
    {
        await _store.RecordAsync(Attestation(end: Now.AddYears(1)));

        var found = await _store.FindInForceAsync("t", "m-1", "NEW-PLAN", Now);
        found.Should().BeEquivalentTo(Attestation(end: Now.AddYears(1)));

        (await _store.FindInForceAsync("t", "m-1", "OTHER-PLAN", Now)).Should().BeNull();
        (await _store.FindInForceAsync("t", "m-2", "NEW-PLAN", Now)).Should().BeNull();
        (await _store.FindInForceAsync("t2", "m-1", "NEW-PLAN", Now)).Should().BeNull();
    }

    [Fact]
    public async Task A_newer_consent_from_the_same_payer_replaces_the_older()
    {
        await _store.RecordAsync(Attestation(fp: "old", end: Now.AddDays(1)));
        await _store.RecordAsync(Attestation(fp: "new"));

        (await _store.FindInForceAsync("t", "m-1", "NEW-PLAN", Now.AddDays(2)))!.ConsentFingerprint.Should().Be("new");
        (await _database.GetCollection<MongoDB.Bson.BsonDocument>(MongoPayerToPayerConsentAttestationStore.CollectionName)
            .CountDocumentsAsync(MongoDB.Bson.BsonDocument.Parse("{}"))).Should().Be(1);
    }

    [Fact]
    public async Task A_consent_outside_its_period_is_not_found()
    {
        await _store.RecordAsync(Attestation(end: Now.AddHours(1)));

        (await _store.FindInForceAsync("t", "m-1", "NEW-PLAN", Now.AddHours(1))).Should().BeNull();
        (await _store.FindInForceAsync("t", "m-1", "NEW-PLAN", Now.AddDays(-2))).Should().BeNull();
    }
}
