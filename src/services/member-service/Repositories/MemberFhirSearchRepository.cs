using System.Text.RegularExpressions;
using MemberService.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MemberService.Repositories;

/// <summary>
/// The query behind FHIR Patient search (ADR 018). Kept apart from
/// <see cref="IMemberRepository"/>'s general search because FHIR search needs
/// what that one does not give: a total, a stable order across pages, drafts
/// excluded before paging, and given-name, gender and last-updated filters.
/// </summary>
public interface IMemberFhirSearchRepository
{
    /// <summary>
    /// One page of the tenant's non-draft members matching every filter in
    /// <paramref name="query"/>, ordered by member id, and the total match count.
    /// </summary>
    Task<(IReadOnlyList<Member> Items, long Total)> SearchAsync(
        string tenantId, MemberFhirSearchQuery query, CancellationToken ct = default);
}

public sealed class MemberFhirSearchRepository : IMemberFhirSearchRepository
{
    private readonly IMongoCollection<Member> _collection;

    public MemberFhirSearchRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<Member>("Members");
    }

    public async Task<(IReadOnlyList<Member> Items, long Total)> SearchAsync(
        string tenantId, MemberFhirSearchQuery query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(query);

        var filter = BuildFilter(tenantId, query);
        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        if (total == 0 || query.Limit <= 0)
            return (Array.Empty<Member>(), total);

        var items = await _collection.Find(filter)
            .Sort(Builders<Member>.Sort.Ascending(m => m.MemberId).Ascending(m => m.Id))
            .Skip(query.Offset)
            .Limit(query.Limit)
            .ToListAsync(ct);

        return (items, total);
    }

    internal static FilterDefinition<Member> BuildFilter(string tenantId, MemberFhirSearchQuery query)
    {
        var f = Builders<Member>.Filter;
        var filter = f.Eq(m => m.TenantId, tenantId) & f.Ne(m => m.IsDraft, true);

        if (!string.IsNullOrWhiteSpace(query.MemberId))
            filter &= f.Eq(m => m.MemberId, query.MemberId.Trim());

        if (Prefix(query.Family) is { } family)
            filter &= f.Regex(m => m.LastName, family);

        if (Prefix(query.Given) is { } given)
            filter &= f.Or(f.Regex(m => m.FirstName, given), f.Regex(m => m.MiddleName, given));

        if (Prefix(query.Name) is { } name)
        {
            filter &= f.Or(
                f.Regex(m => m.LastName, name),
                f.Regex(m => m.FirstName, name),
                f.Regex(m => m.MiddleName, name));
        }

        if (query.BirthDateFrom is { } dobFrom)
            filter &= f.Gte(m => m.DateOfBirth, AsUtcDate(dobFrom));
        if (query.BirthDateTo is { } dobTo)
            filter &= f.Lt(m => m.DateOfBirth, AsUtcDate(dobTo));

        if (query.LastUpdatedFrom is { } updatedFrom)
            filter &= f.Gte(m => m.LastUpdatedDate, AsUtc(updatedFrom));
        if (query.LastUpdatedTo is { } updatedTo)
            filter &= f.Lt(m => m.LastUpdatedDate, AsUtc(updatedTo));

        if (!string.IsNullOrWhiteSpace(query.Gender))
            filter &= GenderFilter(query.Gender.Trim().ToLowerInvariant());

        return filter;
    }

    /// <summary>
    /// The inverse of the Patient projection's gender mapping: stored "M" is
    /// male, "F" is female, and everything else (including no value) is
    /// projected as unknown. No stored value projects as "other", so it
    /// matches nothing rather than something the projection would not show.
    /// </summary>
    private static FilterDefinition<Member> GenderFilter(string gender)
    {
        var f = Builders<Member>.Filter;
        var male = new BsonRegularExpression("^M$", "i");
        var female = new BsonRegularExpression("^F$", "i");
        return gender switch
        {
            FhirGenderCodes.Male => f.Regex(m => m.Gender, male),
            FhirGenderCodes.Female => f.Regex(m => m.Gender, female),
            FhirGenderCodes.Unknown => f.And(f.Not(f.Regex(m => m.Gender, male)), f.Not(f.Regex(m => m.Gender, female))),
            // {$in: []} matches no document.
            _ => f.In(m => m.MemberId, Array.Empty<string>()),
        };
    }

    /// <summary>A case-insensitive starts-with match on the literal text (FHIR string search).</summary>
    private static BsonRegularExpression? Prefix(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : new BsonRegularExpression("^" + Regex.Escape(text), "i");
    }

    private static DateTime AsUtcDate(DateTime value) => DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
