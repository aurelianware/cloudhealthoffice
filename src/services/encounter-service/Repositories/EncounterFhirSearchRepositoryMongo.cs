using EncounterService.Fhir;
using EncounterService.Models;
using MongoDB.Driver;

namespace EncounterService.Repositories;

/// <summary>MongoDB implementation of <see cref="IEncounterFhirSearchRepository"/>.</summary>
public sealed class EncounterFhirSearchRepositoryMongo : IEncounterFhirSearchRepository
{
    private readonly IMongoCollection<Encounter> _collection;

    public EncounterFhirSearchRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<Encounter>("encounters");
    }

    public async Task<(IReadOnlyList<Encounter> Items, long Total)> SearchAsync(
        string tenantId, EncounterFhirSearchQuery query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(query);

        var filter = BuildFilter(tenantId, query);
        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        if (total == 0 || query.Limit <= 0)
            return (Array.Empty<Encounter>(), total);

        var items = await _collection.Find(filter)
            .Sort(Builders<Encounter>.Sort.Descending(e => e.ServiceDateFrom).Ascending(e => e.Id))
            .Skip(query.Offset)
            .Limit(query.Limit)
            .ToListAsync(ct);

        return (items, total);
    }

    internal static FilterDefinition<Encounter> BuildFilter(string tenantId, EncounterFhirSearchQuery query)
    {
        var f = Builders<Encounter>.Filter;
        var filter = f.Eq(e => e.TenantId, tenantId);

        if (!string.IsNullOrWhiteSpace(query.EncounterId))
            filter &= f.Eq(e => e.Id, query.EncounterId.Trim());

        if (!string.IsNullOrWhiteSpace(query.MemberId))
            filter &= f.Eq(e => e.MemberId, query.MemberId.Trim());

        // FHIR date search on a period is overlap, not containment: an
        // encounter matches date=ge2026-01-01 when any part of its service
        // period falls on or after that instant.
        if (query.ServiceDateFrom is { } from)
            filter &= f.Gte(e => e.ServiceDateTo, AsUtc(from));
        if (query.ServiceDateTo is { } to)
            filter &= f.Lt(e => e.ServiceDateFrom, AsUtc(to));

        if (query.LastUpdatedFrom is { } updatedFrom)
            filter &= f.Gte(e => e.LastUpdatedDate, AsUtc(updatedFrom));
        if (query.LastUpdatedTo is { } updatedTo)
            filter &= f.Lt(e => e.LastUpdatedDate, AsUtc(updatedTo));

        if (query.EncounterType is { } type)
            filter &= f.Eq(e => e.EncounterType, type);

        if (!string.IsNullOrWhiteSpace(query.Status))
            filter &= StatusFilter(query.Status.Trim().ToLowerInvariant());

        if (!string.IsNullOrWhiteSpace(query.ControlNumber))
            filter &= f.Eq(e => e.EncounterControlNumber, query.ControlNumber.Trim());

        if (query.Classes.Count > 0)
            filter &= ClassFilter(query.Classes);

        return filter;
    }

    /// <summary>
    /// The inverse of <c>EncounterFhirProjector.ClassOf</c>, which derives the
    /// class from the place of service. The codes OR; one the projection never
    /// emits adds nothing, so a search for only such codes matches nothing.
    /// </summary>
    private static FilterDefinition<Encounter> ClassFilter(IEnumerable<string> classes)
    {
        var f = Builders<Encounter>.Filter;
        var any = new List<FilterDefinition<Encounter>>();
        foreach (var code in classes)
        {
            if (EncounterFhirProjector.PlacesOfServiceFor(code.Trim()) is not { } match) continue;
            any.Add(match.Excluding
                ? f.Nin(e => e.PlaceOfServiceCode, match.PlacesOfService)
                : f.In(e => e.PlaceOfServiceCode, match.PlacesOfService));
        }
        // {$in: []} matches no document.
        return any.Count == 0 ? f.In(e => e.Id, Array.Empty<string>()) : f.Or(any);
    }

    /// <summary>
    /// The inverse of <c>EncounterFhirProjector.StatusOf</c>: a record is
    /// <c>entered-in-error</c> when it is a void by submission type, by the
    /// 837 frequency code, or by status, and <c>finished</c> otherwise. A code
    /// the projection never emits matches nothing rather than something the
    /// caller would not see in the result.
    /// </summary>
    private static FilterDefinition<Encounter> StatusFilter(string status)
    {
        var f = Builders<Encounter>.Filter;
        var voided = f.Eq(e => e.SubmissionType, SubmissionType.Void)
                     | f.Eq(e => e.Status, EncounterStatus.Voided)
                     | f.Eq(e => e.ClaimFrequencyCode, "8");

        return status switch
        {
            FhirEncounterStatusCodes.EnteredInError => voided,
            FhirEncounterStatusCodes.Finished => f.Not(voided),
            // {$in: []} matches no document.
            _ => f.In(e => e.Id, Array.Empty<string>()),
        };
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
