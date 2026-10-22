using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.Repositories;

/// <summary>MongoDB implementation of <see cref="IClaimFhirSearchRepository"/>.</summary>
public sealed class ClaimFhirSearchRepositoryMongo : IClaimFhirSearchRepository
{
    private readonly IMongoCollection<Claim> _collection;

    public ClaimFhirSearchRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<Claim>("Claims");
    }

    public async Task<(IReadOnlyList<Claim> Items, long Total)> SearchAsync(
        string tenantId, ClaimFhirSearchQuery query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(query);

        var filter = BuildFilter(tenantId, query);
        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        if (total == 0 || query.Limit <= 0)
            return (Array.Empty<Claim>(), total);

        var items = await _collection.Find(filter)
            .Sort(Builders<Claim>.Sort.Descending(c => c.SubmittedDate).Ascending(c => c.ClaimVersionId))
            .Skip(query.Offset)
            .Limit(query.Limit)
            .ToListAsync(ct);

        return (items, total);
    }

    internal static FilterDefinition<Claim> BuildFilter(string tenantId, ClaimFhirSearchQuery query)
    {
        var b = Builders<Claim>.Filter;

        // Current versions only, as ClaimCriteriaFilter defines them: the FHIR
        // id is the chain head, so returning a superseded version would answer
        // with a resource whose own id reads back as something else.
        var filter = b.Eq(c => c.TenantId, tenantId)
                     & b.Eq(c => c.SupersededAt, (DateTime?)null)
                     & b.Ne(c => c.VersionState, ClaimVersionState.Draft);

        if (!string.IsNullOrWhiteSpace(query.ClaimVersionId))
        {
            // Legacy rows predating the version chain carry no ClaimVersionId
            // and Hydrate aliases it to Id, which is what the projection emits
            // as the FHIR id — so an _id search has to match either field.
            var id = query.ClaimVersionId.Trim();
            filter &= b.Or(b.Eq(c => c.ClaimVersionId, id), b.Eq(c => c.Id, id));
        }

        if (!string.IsNullOrWhiteSpace(query.MemberId))
            filter &= b.Eq(c => c.MemberId, query.MemberId.Trim());

        if (query.CreatedFrom is { } createdFrom)
            filter &= b.Gte(c => c.SubmittedDate, AsUtc(createdFrom));
        if (query.CreatedTo is { } createdTo)
            filter &= b.Lt(c => c.SubmittedDate, AsUtc(createdTo));

        if (query.LastUpdatedFrom is { } updatedFrom)
            filter &= b.Gte(c => c.LastUpdatedDate, AsUtc(updatedFrom));
        if (query.LastUpdatedTo is { } updatedTo)
            filter &= b.Lt(c => c.LastUpdatedDate, AsUtc(updatedTo));

        if (query.ClaimType is { } claimType)
            filter &= b.Eq(c => c.ClaimType, claimType);

        if (!string.IsNullOrWhiteSpace(query.Status))
            filter &= StatusFilter(query.Status.Trim().ToLowerInvariant());

        return filter;
    }

    /// <summary>
    /// The inverse of <c>ClaimFhirProjector.StatusOf</c>: a voided claim is
    /// <c>cancelled</c> and every other claim is <c>active</c>. A code the
    /// projection never emits matches nothing rather than something the caller
    /// would not see in the result.
    /// </summary>
    private static FilterDefinition<Claim> StatusFilter(string status)
    {
        var b = Builders<Claim>.Filter;
        var voided = b.Eq(c => c.Status, ClaimStatus.Voided);
        return status switch
        {
            FhirClaimStatusCodes.Cancelled => voided,
            FhirClaimStatusCodes.Active => b.Not(voided),
            // {$in: []} matches no document.
            _ => b.In(c => c.Id, Array.Empty<string>()),
        };
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
