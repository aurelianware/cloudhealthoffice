using CloudHealthOffice.Infrastructure.DocumentStore;
using MemberService.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MemberService.Repositories;

/// <summary>
/// EXAMPLE: Cloud-agnostic repository using IDocumentStore.
/// This repository works on both Azure (Cosmos DB) and DigitalOcean (MongoDB).
/// 
/// To use this pattern:
/// 1. Inject IDocumentStore&lt;Member&gt; instead of CosmosClient
/// 2. Use simple queries with parameter dictionaries
/// 3. Avoid Cosmos-specific features (CONTAINS, complex JOINs)
/// </summary>
public class CloudAgnosticMemberRepository : IMemberRepository
{
    private readonly IDocumentStore<Member> _store;

    public CloudAgnosticMemberRepository(IDocumentStore<Member> store)
    {
        _store = store;
    }

    public async Task<Member?> GetByIdAsync(string tenantId, string id)
    {
        return await _store.GetByIdAsync(id, tenantId);
    }

    public async Task<Member?> GetByMemberIdAsync(string tenantId, string memberId)
    {
        var query = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.memberId = @memberId";
        var parameters = new Dictionary<string, object>
        {
            { "tenantId", tenantId },
            { "memberId", memberId }
        };

        var results = await _store.QueryAsync(query, parameters, tenantId);
        return results.FirstOrDefault();
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
        var query = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var parameters = new Dictionary<string, object> { { "tenantId", tenantId } };

        if (!string.IsNullOrEmpty(groupNumber))
        {
            query += " AND c.groupNumber = @groupNumber";
            parameters["groupNumber"] = groupNumber;
        }

        if (!string.IsNullOrEmpty(lastName))
        {
            // Note: Simplified - CONTAINS is Cosmos-specific, MongoDB will use exact match
            query += " AND c.lastName = @lastName";
            parameters["lastName"] = lastName;
        }

        if (dateOfBirth.HasValue)
        {
            query += " AND c.dateOfBirth = @dateOfBirth";
            parameters["dateOfBirth"] = dateOfBirth.Value;
        }

        if (activeOnly)
        {
            query += " AND c.status = @activeStatus";
            parameters["activeStatus"] = (int)EnrollmentStatus.Active;
        }

        if (subscribersOnly)
        {
            query += " AND c.isSubscriber = true";
        }

        return await _store.QueryWithPaginationAsync(query, parameters, tenantId, pageSize, continuationToken);
    }

    public async Task<List<Member>> GetDependentsAsync(string tenantId, string subscriberMemberId)
    {
        var query = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.subscriberMemberId = @subscriberId AND c.isSubscriber = false";
        var parameters = new Dictionary<string, object>
        {
            { "tenantId", tenantId },
            { "subscriberId", subscriberMemberId }
        };

        var results = await _store.QueryAsync(query, parameters, tenantId);
        return results.ToList();
    }

    public async Task<int> GetCountByGroupAsync(string tenantId, string groupNumber)
    {
        var query = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.groupNumber = @groupNumber";
        var parameters = new Dictionary<string, object>
        {
            { "tenantId", tenantId },
            { "groupNumber", groupNumber }
        };

        return await _store.CountAsync(query, parameters, tenantId);
    }

    public async Task<Member> CreateAsync(Member member)
    {
        member.CreatedDate = DateTime.UtcNow;
        member.LastUpdatedDate = DateTime.UtcNow;
        return await _store.UpsertAsync(member, member.TenantId);
    }

    public async Task<Member> UpdateAsync(Member member)
    {
        member.LastUpdatedDate = DateTime.UtcNow;
        return await _store.UpsertAsync(member, member.TenantId);
    }

    public async Task DeleteAsync(string tenantId, string id)
    {
        await _store.DeleteAsync(id, tenantId);
    }

    public async Task<bool> ExistsAsync(string tenantId, string memberId)
    {
        var member = await GetByMemberIdAsync(tenantId, memberId);
        return member != null;
    }
}
