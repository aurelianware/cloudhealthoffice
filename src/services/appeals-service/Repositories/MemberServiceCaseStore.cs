using AppealsService.Models;
using MongoDB.Driver;

namespace AppealsService.Repositories;

public interface IMemberServiceCaseStore
{
    Task<MemberServiceCase?> GetAsync(string tenant, string id, CancellationToken ct);
    Task<IReadOnlyList<MemberServiceCase>> ListAsync(string tenant, CancellationToken ct);
    Task<bool> RecordDecisionAsync(string tenant, string id, long revision, string encryptedResolution, string actor, DateTime at,
        MemberAppealOutcome? outcome, CancellationToken ct);
    /// <summary>Decided authorization appeals whose outcome is still owed to the PA reporting ledger and due a try at <paramref name="now"/>, across tenants.</summary>
    Task<IReadOnlyList<MemberServiceCase>> ListUnreportedAppealDecisionsAsync(DateTime now, int limit, CancellationToken ct);
    /// <summary>
    /// Records one delivery attempt: accepted (<paramref name="reportedAt"/>), refused for good
    /// (<paramref name="rejectedReason"/>), or neither (retry from <paramref name="nextAttemptAt"/>).
    /// </summary>
    Task RecordReportingAttemptAsync(string tenant, string id, DateTime? reportedAt, string? rejectedReason,
        DateTime? nextAttemptAt, CancellationToken ct);
    Task<MemberServiceCase> CreateOrReadAsync(MemberServiceCase record, CancellationToken ct);
}

public sealed class MemberServiceCaseStore(IMongoDatabase db) : IMemberServiceCaseStore
{
    private readonly IMongoCollection<MemberServiceCase> _rows = db.GetCollection<MemberServiceCase>(CollectionName);
    public const string CollectionName = "MemberServiceCases";
    public Task<MemberServiceCase?> GetAsync(string tenant, string id, CancellationToken ct) =>
        _rows.Find(r => r.TenantId == tenant && r.Id == id).FirstOrDefaultAsync(ct)!;

    public async Task<IReadOnlyList<MemberServiceCase>> ListAsync(string tenant, CancellationToken ct) =>
        await _rows.Find(r => r.TenantId == tenant).SortByDescending(r => r.ReceivedAt).Limit(100).ToListAsync(ct);

    public async Task<bool> RecordDecisionAsync(string tenant, string id, long revision, string encryptedResolution,
        string actor, DateTime at, MemberAppealOutcome? outcome, CancellationToken ct)
    {
        var update = Builders<MemberServiceCase>.Update.Set(r => r.Status, "DecisionRecorded")
            .Set(r => r.EncryptedResolution, encryptedResolution).Set(r => r.DecisionRecordedAt, at)
            .Set(r => r.DecisionOutcome, outcome)
            .Set(r => r.DecisionRecordedBy, actor).Set(r => r.Receipt.Status, "DecisionRecorded").Inc(r => r.Revision, 1);
        return (await _rows.UpdateOneAsync(r => r.TenantId == tenant && r.Id == id
            && r.Revision == revision && r.Status == "Received", update, cancellationToken: ct)).ModifiedCount == 1;
    }

    public async Task<IReadOnlyList<MemberServiceCase>> ListUnreportedAppealDecisionsAsync(DateTime now, int limit,
        CancellationToken ct) =>
        await _rows.Find(r => r.Status == "DecisionRecorded" && r.DecisionOutcome != null && r.AuthorizationId != null
                && r.ReportedToAuthorizationAt == null && r.ReportingRejectedReason == null
                && (r.ReportingNextAttemptAt == null || r.ReportingNextAttemptAt <= now))
            .SortBy(r => r.ReportingNextAttemptAt).ThenBy(r => r.DecisionRecordedAt)
            .Limit(Math.Clamp(limit, 1, 500)).ToListAsync(ct);

    public Task RecordReportingAttemptAsync(string tenant, string id, DateTime? reportedAt, string? rejectedReason,
        DateTime? nextAttemptAt, CancellationToken ct)
    {
        // Reporting state only: the case's revision is untouched so a reviewer's view never goes stale on it.
        var update = Builders<MemberServiceCase>.Update.Inc(r => r.ReportingAttempts, 1)
            .Set(r => r.ReportingNextAttemptAt, nextAttemptAt);
        if (reportedAt is { } at) update = update.Set(r => r.ReportedToAuthorizationAt, at);
        if (rejectedReason is not null) update = update.Set(r => r.ReportingRejectedReason, rejectedReason);
        return _rows.UpdateOneAsync(r => r.TenantId == tenant && r.Id == id && r.ReportedToAuthorizationAt == null,
            update, cancellationToken: ct);
    }

    public async Task<MemberServiceCase> CreateOrReadAsync(MemberServiceCase record, CancellationToken ct)
    {
        // Id is a tenant/contact-derived identity, protected by Mongo's _id uniqueness.
        try { await _rows.InsertOneAsync(record, cancellationToken: ct); return record; }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return await GetAsync(record.TenantId, record.Id, ct)
                ?? throw new InvalidOperationException("Conflicting intake identity.");
        }
    }
}
