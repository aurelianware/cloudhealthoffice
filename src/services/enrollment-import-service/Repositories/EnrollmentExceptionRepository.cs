using CloudHealthOffice.Infrastructure.Data;
using EnrollmentImportService.Models;
using MongoDB.Driver;

namespace EnrollmentImportService.Repositories;

public sealed class EnrollmentExceptionConcurrencyException(string message) : Exception(message);

/// <summary>Enrollment exceptions. Every query is scoped to one tenant.</summary>
public interface IEnrollmentExceptionRepository
{
    /// <summary>False when it already exists (the same rejected row recorded twice).</summary>
    Task<bool> TryCreateAsync(EnrollmentException exception, CancellationToken ct = default);
    Task<EnrollmentException?> GetAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Replaces it if its stored version is still <paramref name="expectedVersion"/>; bumps the version.</summary>
    Task SaveAsync(EnrollmentException exception, long expectedVersion, CancellationToken ct = default);

    Task<IReadOnlyList<EnrollmentException>> ListAsync(string tenantId, EnrollmentExceptionQuery query, CancellationToken ct = default);

    /// <summary>Open exceptions, and held ones whose hold has run out at <paramref name="now"/>, oldest first.</summary>
    Task<(IReadOnlyList<EnrollmentException> Items, long Total)> ListWorkableAsync(string tenantId, DateTime now, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Unresolved dependents (Open or Held) rejected because the subscriber
    /// <paramref name="subscriberId"/> was not on file: what to retry when it arrives.
    /// </summary>
    Task<IReadOnlyList<EnrollmentException>> ListOrphansForSubscriberAsync(string tenantId, string subscriberId, CancellationToken ct = default);

    /// <summary>Tenants with an automatically held orphan whose grace period has ended at <paramref name="now"/>.</summary>
    Task<IReadOnlyList<string>> ListTenantsWithExpiredAutoHeldAsync(DateTime now, CancellationToken ct = default);

    /// <summary>Automatically held orphans whose grace period has ended, oldest hold first.</summary>
    Task<IReadOnlyList<EnrollmentException>> ListExpiredAutoHeldAsync(string tenantId, DateTime now, int limit, CancellationToken ct = default);
}

public sealed class EnrollmentExceptionQuery
{
    public EnrollmentExceptionStatus? Status { get; set; }
    public string? ReasonCode { get; set; }
    public string? GroupNumber { get; set; }
    public string? MemberId { get; set; }
    public string? EdiFileId { get; set; }
    public int Limit { get; set; } = 100;
}

public sealed class EnrollmentExceptionRepository : IEnrollmentExceptionRepository
{
    public const string CollectionName = "enrollment-exceptions";

    private readonly IMongoCollection<EnrollmentException> _collection;

    public EnrollmentExceptionRepository(IMongoDatabase database) =>
        _collection = database.GetCollection<EnrollmentException>(CollectionName);

    public async Task<bool> TryCreateAsync(EnrollmentException exception, CancellationToken ct = default)
    {
        try
        {
            await _collection.InsertOneAsync(exception, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<EnrollmentException?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _collection.Find(x => x.TenantId == tenantId && x.Id == id).FirstOrDefaultAsync(ct);

    public async Task SaveAsync(EnrollmentException exception, long expectedVersion, CancellationToken ct = default)
    {
        exception.Version = expectedVersion + 1;
        var result = await _collection.ReplaceOneAsync(
            x => x.TenantId == exception.TenantId && x.Id == exception.Id && x.Version == expectedVersion,
            exception, cancellationToken: ct);
        if (result.MatchedCount == 0)
        {
            exception.Version = expectedVersion;
            throw new EnrollmentExceptionConcurrencyException($"Exception {exception.Id} changed since it was read.");
        }
    }

    public async Task<IReadOnlyList<EnrollmentException>> ListAsync(string tenantId, EnrollmentExceptionQuery query, CancellationToken ct = default)
    {
        var f = Builders<EnrollmentException>.Filter;
        var filter = f.Eq(x => x.TenantId, tenantId);
        if (query.Status is { } status) filter &= f.Eq(x => x.Status, status);
        if (!string.IsNullOrWhiteSpace(query.ReasonCode)) filter &= f.Eq(x => x.ReasonCode, query.ReasonCode);
        if (!string.IsNullOrWhiteSpace(query.GroupNumber)) filter &= f.Eq(x => x.GroupNumber, query.GroupNumber);
        if (!string.IsNullOrWhiteSpace(query.MemberId)) filter &= f.Eq(x => x.MemberId, query.MemberId);
        if (!string.IsNullOrWhiteSpace(query.EdiFileId)) filter &= f.Eq(x => x.EdiFileId, query.EdiFileId);
        return await _collection.Find(filter)
            .SortByDescending(x => x.CreatedAt)
            .Limit(Math.Clamp(query.Limit, 1, 500))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EnrollmentException>> ListOrphansForSubscriberAsync(string tenantId, string subscriberId, CancellationToken ct = default)
    {
        var f = Builders<EnrollmentException>.Filter;
        var filter = f.Eq(x => x.TenantId, tenantId)
                     & f.Eq(x => x.ReasonCode, EnrollmentRejectionReasons.SubscriberNotFound)
                     & f.Eq(x => x.Dependent!.SubscriberId, subscriberId)
                     & f.Eq(x => x.SourceKind, EnrollmentExceptionSourceKind.Dependent)
                     & f.In(x => x.Status, new[] { EnrollmentExceptionStatus.Open, EnrollmentExceptionStatus.Held });
        return await _collection.Find(filter).SortBy(x => x.CreatedAt).Limit(200).ToListAsync(ct);
    }

    [CrossTenantQuery("The orphan sweep finds the tenants with ended grace periods, then works each under its own tenant (ListExpiredAutoHeldAsync).")]
    public async Task<IReadOnlyList<string>> ListTenantsWithExpiredAutoHeldAsync(DateTime now, CancellationToken ct = default)
    {
        var f = Builders<EnrollmentException>.Filter;
        var filter = f.Eq(x => x.AutoHeld, true) & f.Eq(x => x.Status, EnrollmentExceptionStatus.Held) & f.Lte(x => x.HeldUntil, now);
        using var cursor = await _collection.DistinctAsync(x => x.TenantId, filter, cancellationToken: ct);
        return await cursor.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EnrollmentException>> ListExpiredAutoHeldAsync(string tenantId, DateTime now, int limit, CancellationToken ct = default)
    {
        var f = Builders<EnrollmentException>.Filter;
        var filter = f.Eq(x => x.TenantId, tenantId) & f.Eq(x => x.AutoHeld, true)
                     & f.Eq(x => x.Status, EnrollmentExceptionStatus.Held) & f.Lte(x => x.HeldUntil, now);
        return await _collection.Find(filter).SortBy(x => x.HeldUntil).Limit(Math.Clamp(limit, 1, 1000)).ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<EnrollmentException> Items, long Total)> ListWorkableAsync(
        string tenantId, DateTime now, int page, int pageSize, CancellationToken ct = default)
    {
        var f = Builders<EnrollmentException>.Filter;
        var filter = f.Eq(x => x.TenantId, tenantId) &
                     (f.Eq(x => x.Status, EnrollmentExceptionStatus.Open) |
                      (f.Eq(x => x.Status, EnrollmentExceptionStatus.Held) & f.Lte(x => x.HeldUntil, now)));
        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await _collection.Find(filter)
            .SortBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((Math.Max(1, page) - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }
}
