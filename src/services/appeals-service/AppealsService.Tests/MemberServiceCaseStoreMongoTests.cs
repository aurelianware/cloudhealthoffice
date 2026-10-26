using AppealsService.Models;
using AppealsService.Repositories;
using CloudHealthOffice.Infrastructure.Contacts;
using CloudHealthOffice.Testing.Mongo;
using MongoDB.Driver;

namespace AppealsService.Tests;

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MemberServiceCaseStoreMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    public Task InitializeAsync() { _db = mongo.CreateDatabase("member_service_cases"); return Task.CompletedTask; }
    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);
    private static MemberServiceCase Case() => new() {
        Id = ContactIntakeIdentity.For("tenant-1", "synthetic-contact"), TenantId = "tenant-1",
        ContactId = "synthetic-contact", MemberId = "synthetic-member", EncryptedSummary = "synthetic-ciphertext",
        ReceivedAt = new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc),
        PinnedRuleEvaluationJson = "{\"version\":\"synthetic-v1\"}",
        Receipt = new() { Id = ContactIntakeIdentity.For("tenant-1", "synthetic-contact"), Kind = "Grievance",
            Status = "Received", RulePackVersion = "synthetic-v1" }
    };

    [Fact]
    public async Task Concurrent_intake_retries_share_one_pinned_record()
    {
        var store = new MemberServiceCaseStore(_db);
        var records = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.CreateOrReadAsync(Case(), default)));
        Assert.Single(records.Select(r => r.Id).Distinct());
        Assert.Single(await store.ListAsync("tenant-1", default));
        Assert.Null(await store.GetAsync("other-tenant", Case().Id, default));
        Assert.All(records, r => Assert.Equal("synthetic-v1", r.Receipt.RulePackVersion));
    }

    [Fact]
    public async Task Concurrent_review_records_one_decision_without_changing_receipt_or_rule_snapshot()
    {
        var store = new MemberServiceCaseStore(_db);
        var original = await store.CreateOrReadAsync(Case(), default);
        var writes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => store.RecordDecisionAsync(
            "tenant-1", original.Id, 0, "synthetic-encrypted-resolution", "reviewer-" + i, DateTime.UtcNow, default)));
        Assert.Single(writes.Where(w => w));
        var stored = (await store.GetAsync("tenant-1", original.Id, default))!;
        Assert.Equal(1, stored.Revision);
        Assert.Equal("DecisionRecorded", stored.Status);
        Assert.Equal("DecisionRecorded", stored.Receipt.Status);
        Assert.Equal(original.ReceivedAt, stored.ReceivedAt);
        Assert.Equal(original.PinnedRuleEvaluationJson, stored.PinnedRuleEvaluationJson);
        Assert.False(await store.RecordDecisionAsync("other-tenant", original.Id, 1, "x", "x", DateTime.UtcNow, default));
        Assert.False(await store.RecordDecisionAsync("tenant-1", original.Id, 1, "x", "x", DateTime.UtcNow, default));
    }
}
