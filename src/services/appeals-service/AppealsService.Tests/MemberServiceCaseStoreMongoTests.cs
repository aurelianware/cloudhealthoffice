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
            "tenant-1", original.Id, 0, "synthetic-encrypted-resolution", "reviewer-" + i, DateTime.UtcNow, null, default)));
        Assert.Single(writes, w => w);
        var stored = (await store.GetAsync("tenant-1", original.Id, default))!;
        Assert.Equal(1, stored.Revision);
        Assert.Equal("DecisionRecorded", stored.Status);
        Assert.Equal("DecisionRecorded", stored.Receipt.Status);
        Assert.Equal(original.ReceivedAt, stored.ReceivedAt);
        Assert.Equal(original.PinnedRuleEvaluationJson, stored.PinnedRuleEvaluationJson);
        Assert.False(await store.RecordDecisionAsync("other-tenant", original.Id, 1, "x", "x", DateTime.UtcNow, null, default));
        Assert.False(await store.RecordDecisionAsync("tenant-1", original.Id, 1, "x", "x", DateTime.UtcNow, null, default));
    }

    [Fact]
    public async Task Decided_appeals_stay_owed_to_the_ledger_until_accepted_or_refused()
    {
        var store = new MemberServiceCaseStore(_db);
        var appeal = Case(); appeal.Classification = "Appeal"; appeal.AuthorizationId = "auth-1";
        var grievance = Case(); grievance.Id = ContactIntakeIdentity.For("tenant-1", "synthetic-grievance");
        await store.CreateOrReadAsync(appeal, default);
        await store.CreateOrReadAsync(grievance, default);
        var at = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(await store.RecordDecisionAsync("tenant-1", appeal.Id, 0, "c", "r", at, MemberAppealOutcome.Overturned, default));
        Assert.True(await store.RecordDecisionAsync("tenant-1", grievance.Id, 0, "c", "r", at, null, default));

        var now = DateTime.UtcNow;
        var owed = Assert.Single(await store.ListUnreportedAppealDecisionsAsync(now, 10, default));
        Assert.Equal(appeal.Id, owed.Id);
        Assert.Equal(MemberAppealOutcome.Overturned, owed.DecisionOutcome);

        await store.RecordReportingAttemptAsync("tenant-1", appeal.Id, null, null, now.AddMinutes(5), default);
        Assert.Empty(await store.ListUnreportedAppealDecisionsAsync(now, 10, default));
        Assert.Single(await store.ListUnreportedAppealDecisionsAsync(now.AddMinutes(6), 10, default));

        await store.RecordReportingAttemptAsync("tenant-1", appeal.Id, now, null, null, default);
        Assert.Empty(await store.ListUnreportedAppealDecisionsAsync(now.AddDays(1), 10, default));
        var stored = (await store.GetAsync("tenant-1", appeal.Id, default))!;
        Assert.Equal(2, stored.ReportingAttempts);
        Assert.Equal(1, stored.Revision);
        Assert.NotNull(stored.ReportedToAuthorizationAt);
    }

    [Fact]
    public async Task Refused_outcomes_are_listed_per_tenant_and_requeued_once_with_their_refusal_kept()
    {
        var store = new MemberServiceCaseStore(_db);
        var appeal = Case(); appeal.Classification = "Appeal"; appeal.AuthorizationId = "auth-1";
        await store.CreateOrReadAsync(appeal, default);
        var at = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(await store.RecordDecisionAsync("tenant-1", appeal.Id, 0, "c", "r", at, MemberAppealOutcome.Upheld, default));
        await store.RecordReportingAttemptAsync("tenant-1", appeal.Id, null, "refused (400).", null, default);

        Assert.Empty(await store.ListUnreportedAppealDecisionsAsync(DateTime.UtcNow, 10, default));
        Assert.Empty(await store.ListReportingNeedsAttentionAsync("other-tenant", 10, default));
        var refused = Assert.Single(await store.ListReportingNeedsAttentionAsync("tenant-1", 10, default));

        var writes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            store.RequeueRefusedReportingAsync(refused, "admin-" + i, DateTime.UtcNow, default)));
        Assert.Single(writes, w => w);
        Assert.Empty(await store.ListReportingNeedsAttentionAsync("tenant-1", 10, default));
        var owed = Assert.Single(await store.ListUnreportedAppealDecisionsAsync(DateTime.UtcNow, 10, default));
        Assert.Equal(0, owed.ReportingAttempts);
        Assert.Null(owed.ReportingRejectedReason);
        Assert.Equal(1, owed.Revision);
        var requeue = Assert.Single(owed.ReportingRequeues);
        Assert.Equal("refused (400).", requeue.PriorReason);
        Assert.Equal(1, requeue.PriorAttempts);
        Assert.False(await store.RequeueRefusedReportingAsync(owed, "admin", DateTime.UtcNow, default));
    }
}
