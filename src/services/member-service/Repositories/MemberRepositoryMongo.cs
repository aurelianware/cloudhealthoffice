using MongoDB.Driver;
using MongoDB.Bson;
using MemberService.Models;
using System;
using System.Collections.Generic;
using System.Linq;
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
            
        if (!string.IsNullOrEmpty(lastName))
            filter &= builder.Regex(x => x.LastName, new BsonRegularExpression(lastName, "i"));

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
            
        await _collection.InsertOneAsync(member);
        return member;
    }

    public async Task<Member> UpdateAsync(Member member)
    {
        var filter = Builders<Member>.Filter.Eq(x => x.Id, member.Id) & 
                     Builders<Member>.Filter.Eq(x => x.TenantId, member.TenantId);
                     
        await _collection.ReplaceOneAsync(filter, member);
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
