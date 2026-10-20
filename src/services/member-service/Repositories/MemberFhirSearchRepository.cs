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

    /// <summary>
    /// Each member's subscriber, as the member read derives it: the active
    /// family-relationship graph first (the earliest-starting dependent edge),
    /// else the legacy recorded value. Subscribers and members with neither map
    /// to null. One query for the whole page.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> SubscribersAsync(
        string tenantId, IReadOnlyList<Member> members, CancellationToken ct = default);
}

public sealed class MemberFhirSearchRepository : IMemberFhirSearchRepository
{
    private readonly IMongoCollection<Member> _collection;
    private readonly IMongoCollection<FamilyRelationship> _relationships;

    public MemberFhirSearchRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<Member>("Members");
        _relationships = database.GetCollection<FamilyRelationship>(FamilyRelationshipRepositoryMongo.CollectionName);
    }

    public async Task<(IReadOnlyList<Member> Items, long Total)> SearchAsync(
        string tenantId, MemberFhirSearchQuery query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(query);

        var filter = BuildFilter(tenantId, query);
        if (!string.IsNullOrWhiteSpace(query.SubscriberMemberId))
            filter &= await SubscriberFilterAsync(tenantId, query.SubscriberMemberId.Trim(), query.AnyIdentifierForm, ct);
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

    /// <summary>
    /// Members whose subscriber may be <paramref name="subscriber"/>: the subjects
    /// of active dependent edges pointing at it, and members whose legacy recorded
    /// subscriber is it. A superset of the members whose derived subscriber is
    /// <paramref name="subscriber"/>; each result carries its derived subscriber
    /// (<see cref="SubscribersAsync"/>) so a caller can tell them apart.
    /// </summary>
    private async Task<FilterDefinition<Member>> SubscriberFilterAsync(
        string tenantId, string subscriber, bool anyForm, CancellationToken ct)
    {
        var pattern = anyForm ? AnyForm(subscriber) : null;
        var rf = Builders<FamilyRelationship>.Filter;
        var related = pattern is null ? rf.Eq(r => r.RelatedMemberId, subscriber) : rf.Regex(r => r.RelatedMemberId, pattern);
        var dependents = await _relationships
            .Find(ActiveDependentEdges(tenantId) & related)
            .Project(r => r.SubjectMemberId)
            .ToListAsync(ct);

        var f = Builders<Member>.Filter;
#pragma warning disable CS0618 // the legacy field is still the fallback the member read uses
        var legacy = (pattern is null ? f.Eq(m => m.SubscriberMemberId, subscriber) : f.Regex(m => m.SubscriberMemberId, pattern))
                     & f.Ne(m => m.IsSubscriber, true);
#pragma warning restore CS0618
        return dependents.Count == 0 ? legacy : f.Or(f.In(m => m.MemberId, dependents.Distinct()), legacy);
    }

    public async Task<IReadOnlyDictionary<string, string?>> SubscribersAsync(
        string tenantId, IReadOnlyList<Member> members, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var dependents = members.Where(m => !m.IsSubscriber).Select(m => m.MemberId).Distinct().ToList();
        foreach (var m in members) result[m.MemberId] = null;
        if (dependents.Count == 0) return result;

        var edges = await _relationships
            .Find(ActiveDependentEdges(tenantId) & Builders<FamilyRelationship>.Filter.In(r => r.SubjectMemberId, dependents))
            .ToListAsync(ct);
        var derived = edges
            .GroupBy(e => e.SubjectMemberId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.StartDate).First().RelatedMemberId, StringComparer.Ordinal);

        foreach (var member in members.Where(m => !m.IsSubscriber))
        {
#pragma warning disable CS0618
            var legacy = string.IsNullOrWhiteSpace(member.SubscriberMemberId) ? null : member.SubscriberMemberId;
#pragma warning restore CS0618
            result[member.MemberId] = derived.TryGetValue(member.MemberId, out var fromGraph) ? fromGraph : legacy;
        }
        return result;
    }

    /// <summary>Not deleted, not ended, and a dependent-to-subscriber code (as FamilyRelationshipService derives).</summary>
    private static FilterDefinition<FamilyRelationship> ActiveDependentEdges(string tenantId)
    {
        var fb = Builders<FamilyRelationship>.Filter;
        return fb.Eq(r => r.TenantId, tenantId)
               & fb.Eq(r => r.DeletedAt, (DateTime?)null)
               & (fb.Eq(r => r.EndDate, (DateTime?)null) | fb.Gt(r => r.EndDate, DateTime.UtcNow))
               & fb.In(r => r.RelationshipCode, Services.FamilyRelationshipService.DependentToSubscriberCodes);
    }

    internal static FilterDefinition<Member> BuildFilter(string tenantId, MemberFhirSearchQuery query)
    {
        var f = Builders<Member>.Filter;
        var filter = f.Eq(m => m.TenantId, tenantId) & f.Ne(m => m.IsDraft, true);

        if (!string.IsNullOrWhiteSpace(query.MemberId))
        {
            filter &= query.AnyIdentifierForm && AnyForm(query.MemberId) is { } anyForm
                ? f.Regex(m => m.MemberId, anyForm)
                : f.Eq(m => m.MemberId, query.MemberId.Trim());
        }


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

    /// <summary>
    /// Every stored spelling of an identifier that normalizes to the same value
    /// as <paramref name="value"/> (case, spaces and hyphens ignored, as
    /// Payer-to-Payer member match normalizes): <c>AB12</c> matches <c>ab-12</c>
    /// and <c>A B 12</c>. Null for a value with nothing left after normalizing.
    /// Anchored and case-insensitive, so it scans the tenant's index keys rather
    /// than seeking; bounded by one tenant.
    /// </summary>
    internal static BsonRegularExpression? AnyForm(string? value)
    {
        // The hyphens are literal characters: MongoDB's regex engine has no \u escape.
        const string separators = "[\\s\\-\u2010\u2011]*";
        var core = (value ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '\u2010' or '\u2011')).ToList();
        if (core.Count == 0) return null;
        var pattern = "^" + separators + string.Join(separators, core.Select(c => Regex.Escape(c.ToString()))) + separators + "$";
        return new BsonRegularExpression(pattern, "i");
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
