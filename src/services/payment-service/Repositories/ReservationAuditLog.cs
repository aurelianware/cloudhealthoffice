using MongoDB.Driver;
using PaymentService.Models;

namespace PaymentService.Repositories;

/// <summary>
/// Append-only audit of claim-reservation releases and flags (who, which run,
/// which claim, which tenant, why). Tenant is explicit: the reconciliation job
/// writes it outside any request.
/// </summary>
public interface IReservationAuditLog
{
    Task RecordAsync(ReservationAuditEntry entry);

    /// <summary>A tenant's entries, newest first; one run's only when <paramref name="runId"/> is given.</summary>
    Task<IReadOnlyList<ReservationAuditEntry>> ListAsync(string tenantId, string? runId = null);
}

public sealed class ReservationAuditLogMongo : IReservationAuditLog
{
    public const string CollectionName = "PaymentReservationAudit";

    private readonly IMongoCollection<ReservationAuditEntry> _collection;

    public ReservationAuditLogMongo(IMongoDatabase database)
        => _collection = database.GetCollection<ReservationAuditEntry>(CollectionName);

    public Task RecordAsync(ReservationAuditEntry entry) => _collection.InsertOneAsync(entry);

    public async Task<IReadOnlyList<ReservationAuditEntry>> ListAsync(string tenantId, string? runId = null)
    {
        var filter = Builders<ReservationAuditEntry>.Filter.Eq(x => x.TenantId, tenantId);
        if (!string.IsNullOrEmpty(runId))
            filter &= Builders<ReservationAuditEntry>.Filter.Eq(x => x.RunId, runId);
        return await _collection.Find(filter).SortByDescending(x => x.At).ToListAsync();
    }
}
