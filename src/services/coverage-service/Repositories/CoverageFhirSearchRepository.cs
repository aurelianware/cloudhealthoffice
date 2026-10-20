using System.Text.Json.Nodes;
using CoverageService.Models;
using MongoDB.Driver;

namespace CoverageService.Repositories;

/// <summary>
/// Structured query behind <c>GET /api/v1/coverage/fhir/Coverage</c> (ADR 018).
/// fhir-service parses FHIR search syntax; this service receives resolved
/// filters, which combine with AND.
/// </summary>
public sealed class CoverageFhirSearchQuery
{
    public const int MaxLimit = 100;
    public const int MaxOffset = 10_000;

    /// <summary>Exact coverage id (FHIR <c>_id</c>).</summary>
    public string? CoverageId { get; init; }

    /// <summary>Exact member id (FHIR <c>patient</c> / <c>beneficiary</c>).</summary>
    public string? MemberId { get; init; }

    /// <summary>CHO statuses to include; empty means any.</summary>
    public IReadOnlyList<CoverageStatus> Statuses { get; init; } = [];

    /// <summary>Lines of business to include; empty means any.</summary>
    public IReadOnlyList<LineOfBusiness> LinesOfBusiness { get; init; } = [];

    public DateTime? LastUpdatedFrom { get; init; }

    public DateTime? LastUpdatedTo { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; } = 20;
}

/// <summary>One projected Coverage, with the ids the record-access audit logs.</summary>
public sealed record CoverageFhirSearchEntry(string Id, string MemberId, JsonObject Resource);

public sealed record CoverageFhirSearchResponse(long Total, IReadOnlyList<CoverageFhirSearchEntry> Items);

public interface ICoverageFhirSearchRepository
{
    /// <summary>
    /// One page of the tenant's coverage matching every filter, ordered by member
    /// id, then most recent effective date, then id; and the total match count.
    /// </summary>
    Task<(IReadOnlyList<Coverage> Items, long Total)> SearchAsync(
        string tenantId, CoverageFhirSearchQuery query, CancellationToken ct = default);
}

public sealed class CoverageFhirSearchRepository : ICoverageFhirSearchRepository
{
    private readonly IMongoCollection<Coverage> _collection;

    public CoverageFhirSearchRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<Coverage>(CoverageRepositoryMongo.CollectionName);
    }

    public async Task<(IReadOnlyList<Coverage> Items, long Total)> SearchAsync(
        string tenantId, CoverageFhirSearchQuery query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(query);

        var filter = BuildFilter(tenantId, query);
        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        if (total == 0 || query.Limit <= 0)
            return (Array.Empty<Coverage>(), total);

        var items = await _collection.Find(filter)
            .Sort(Builders<Coverage>.Sort
                .Ascending(c => c.MemberId)
                .Descending(c => c.EffectiveDate)
                .Ascending(c => c.Id))
            .Skip(query.Offset)
            .Limit(query.Limit)
            .ToListAsync(ct);

        return (items, total);
    }

    internal static FilterDefinition<Coverage> BuildFilter(string tenantId, CoverageFhirSearchQuery query)
    {
        var f = Builders<Coverage>.Filter;
        var filter = f.Eq(c => c.TenantId, tenantId);

        if (!string.IsNullOrWhiteSpace(query.CoverageId))
            filter &= f.Eq(c => c.Id, query.CoverageId.Trim());
        if (!string.IsNullOrWhiteSpace(query.MemberId))
            filter &= f.Eq(c => c.MemberId, query.MemberId.Trim());
        if (query.Statuses.Count > 0)
            filter &= f.In(c => c.Status, query.Statuses);
        if (query.LinesOfBusiness.Count > 0)
            filter &= f.In(c => c.LineOfBusiness, query.LinesOfBusiness);
        if (query.LastUpdatedFrom is { } from)
            filter &= f.Gte(c => c.LastUpdatedDate, AsUtc(from));
        if (query.LastUpdatedTo is { } to)
            filter &= f.Lt(c => c.LastUpdatedDate, AsUtc(to));

        return filter;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
