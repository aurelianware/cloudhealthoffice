using AppealsService.Models;
using MongoDB.Driver;

namespace AppealsService.Repositories;

public interface IMemberServiceCaseStore
{
    Task<MemberServiceCase?> GetAsync(string tenant, string id, CancellationToken ct);
    Task<IReadOnlyList<MemberServiceCase>> ListAsync(string tenant, CancellationToken ct);
    Task<bool> RecordDecisionAsync(string tenant, string id, long revision, string encryptedResolution, string actor, DateTime at, CancellationToken ct);
    Task<MemberServiceCase> CreateOrReadAsync(MemberServiceCase record, CancellationToken ct);
}

public sealed class MemberServiceCaseStore(IMongoDatabase db) : IMemberServiceCaseStore
{
    private readonly IMongoCollection<MemberServiceCase> _rows = db.GetCollection<MemberServiceCase>("MemberServiceCases");
    public Task<MemberServiceCase?> GetAsync(string tenant, string id, CancellationToken ct) =>
        _rows.Find(r => r.TenantId == tenant && r.Id == id).FirstOrDefaultAsync(ct)!;

    public async Task<IReadOnlyList<MemberServiceCase>> ListAsync(string tenant, CancellationToken ct) =>
        await _rows.Find(r => r.TenantId == tenant).SortByDescending(r => r.ReceivedAt).Limit(100).ToListAsync(ct);

    public async Task<bool> RecordDecisionAsync(string tenant, string id, long revision, string encryptedResolution,
        string actor, DateTime at, CancellationToken ct)
    {
        var update = Builders<MemberServiceCase>.Update.Set(r => r.Status, "DecisionRecorded")
            .Set(r => r.EncryptedResolution, encryptedResolution).Set(r => r.DecisionRecordedAt, at)
            .Set(r => r.DecisionRecordedBy, actor).Set(r => r.Receipt.Status, "DecisionRecorded").Inc(r => r.Revision, 1);
        return (await _rows.UpdateOneAsync(r => r.TenantId == tenant && r.Id == id
            && r.Revision == revision && r.Status == "Received", update, cancellationToken: ct)).ModifiedCount == 1;
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
