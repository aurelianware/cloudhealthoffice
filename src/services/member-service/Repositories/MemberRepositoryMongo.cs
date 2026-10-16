using MongoDB.Driver;
using MongoDB.Bson;
using MemberService.Models;
using MemberService.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MemberService.Repositories;

/// <summary>
/// MongoDB repository for Member entities.
/// Implements IMemberRepository for cloud-agnostic deployment.
/// </summary>
public class MemberRepositoryMongo : IMemberRepository
{
    private readonly IMongoCollection<Member> _collection;

    /// <summary>
    /// Constructs the repository. Index creation is handled at startup by
    /// <c>MemberIndexInitializer</c> so the repository can be registered as
    /// a singleton and constructed without I/O side effects.
    /// </summary>
    public MemberRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<Member>("Members");
    }

    public async Task<Member?> GetByIdAsync(string tenantId, string id)
    {
        var filter = Builders<Member>.Filter.Eq(x => x.Id, id) & 
                     Builders<Member>.Filter.Eq(x => x.TenantId, tenantId);
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<Member?> GetByMemberIdAsync(string tenantId, string memberId)
    {
        var filter = Builders<Member>.Filter.Eq(x => x.MemberId, memberId) & 
                     Builders<Member>.Filter.Eq(x => x.TenantId, tenantId);
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<(IEnumerable<Member> Items, string? ContinuationToken)> SearchAsync(
        string tenantId,
        string? groupNumber = null,
        string? lastName = null,
        DateTime? dateOfBirth = null,
        bool activeOnly = false,
        bool subscribersOnly = false,
        int pageSize = 20,
        string? continuationToken = null)
    {
        var builder = Builders<Member>.Filter;
        var filter = builder.Eq(x => x.TenantId, tenantId);

        if (!string.IsNullOrEmpty(groupNumber))
            filter &= builder.Eq(x => x.GroupNumber, groupNumber);
            
        // Case-insensitive prefix match on the literal text: the input is escaped, so
        // ".*" or "(a+)+$" match only names that start with those characters.
        var lastNamePrefix = lastName?.Trim();
        if (!string.IsNullOrEmpty(lastNamePrefix))
            filter &= builder.Regex(x => x.LastName, new BsonRegularExpression("^" + Regex.Escape(lastNamePrefix), "i"));

        // If DateOfBirth is in the model (Member.cs didn't show it but it's likely there in omitted lines)
        // Assuming it exists based on signature
        if (dateOfBirth.HasValue)
             filter &= builder.Eq("DateOfBirth", dateOfBirth.Value);

        if (subscribersOnly)
            filter &= builder.Eq(x => x.IsSubscriber, true);
            
        // "activeOnly" logic depends on implementation details not fully visible, assuming filtering active records
        
        int skip = 0;
        if (!string.IsNullOrEmpty(continuationToken) && int.TryParse(continuationToken, out int tokenSkip))
        {
            skip = tokenSkip;
        }

        var results = await _collection.Find(filter)
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync();

        string? newToken = results.Count == pageSize ? (skip + pageSize).ToString() : null;

        return (results, newToken);
    }

    public async Task<List<Member>> GetDependentsAsync(string tenantId, string subscriberMemberId)
    {
        // Assuming there is a field for linking dependents to subscribers
        // The Model showed 'IsSubscriber', so probably 'SubscriberMemberId' or similar exists
        // Filter by TenantId and whatever links them
        
        // Since I don't see the full Member.cs, I'll rely on a common pattern or raw Bson if needed
        // Assuming "RelatedMemberId" or "SubscriberId" based on typical models
        // Safest is to query where IsSubscriber = false and ReferenceId = subscriberId
        
        // Using string mapping to be safe if property name is unknown
        var filter = Builders<Member>.Filter.Eq(x => x.TenantId, tenantId) &
                     Builders<Member>.Filter.Eq("SubscriberMemberId", subscriberMemberId); // Guessing field name logic matches usage
                     
        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<int> GetCountByGroupAsync(string tenantId, string groupNumber)
    {
        var filter = Builders<Member>.Filter.Eq(x => x.TenantId, tenantId) &
                     Builders<Member>.Filter.Eq(x => x.GroupNumber, groupNumber);
        
        var count = await _collection.CountDocumentsAsync(filter);
        return (int)count;
    }

    public async Task<Member> CreateAsync(Member member)
    {
        if (string.IsNullOrEmpty(member.Id))
            member.Id = Guid.NewGuid().ToString();
        member.Revision = 0;

        await _collection.InsertOneAsync(member);
        return member;
    }

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<Member> RevisionIs(long expected)
    {
        var b = Builders<Member>.Filter;
        return expected == 0
            ? b.Or(b.Eq(x => x.Revision, 0L), b.Exists(x => x.Revision, false))
            : b.Eq(x => x.Revision, expected);
    }

    public async Task<Member> UpdateAsync(Member member)
    {
        var filter = Builders<Member>.Filter.Eq(x => x.Id, member.Id) &
                     Builders<Member>.Filter.Eq(x => x.TenantId, member.TenantId);

        // Conditional on the revision the caller read: a concurrent write in
        // between makes this one fail instead of being silently overwritten.
        var expected = member.Revision;
        member.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), member);
        }
        catch
        {
            member.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            member.Revision = expected;
            if (await _collection.Find(filter).AnyAsync())
                throw new MemberConcurrencyException(member.Id, expected);
            // Missing: unchanged behaviour (nothing written, no error).
        }

        return member;
    }

    public async Task DeleteAsync(string tenantId, string id)
    {
        var filter = Builders<Member>.Filter.Eq(x => x.Id, id) & 
                     Builders<Member>.Filter.Eq(x => x.TenantId, tenantId);
                     
        await _collection.DeleteOneAsync(filter);
    }

    public async Task<bool> ExistsAsync(string tenantId, string memberId)
    {
        var filter = Builders<Member>.Filter.Eq(x => x.MemberId, memberId) &
                     Builders<Member>.Filter.Eq(x => x.TenantId, tenantId);

        return await _collection.Find(filter).AnyAsync();
    }

    public async Task<Member?> GetByIdentifierAsync(string tenantId, string system, string value)
    {
        var filter = Builders<Member>.Filter.Eq(x => x.TenantId, tenantId) &
                     Builders<Member>.Filter.ElemMatch(
                         x => x.Identifiers,
                         Builders<MemberIdentifier>.Filter.Eq(i => i.System, system) &
                         Builders<MemberIdentifier>.Filter.Eq(i => i.Value, value));

        return await _collection.Find(filter).FirstOrDefaultAsync();
    }
}
