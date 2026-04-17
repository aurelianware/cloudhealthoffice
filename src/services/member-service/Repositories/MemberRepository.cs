using Microsoft.Azure.Cosmos;
using MemberService.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MemberService.Repositories;

/// <summary>
/// Cosmos DB repository for Member entities.
/// Uses TenantId as partition key for multi-tenant isolation.
/// </summary>
public class MemberRepository : IMemberRepository
{
    private readonly Container _container;
    private const string ContainerName = "Members";
    private const string PartitionKeyPath = "/tenantId";

    public MemberRepository(CosmosClient cosmosClient, string databaseName)
    {
        var database = cosmosClient.GetDatabase(databaseName);
        _container = database.GetContainer(ContainerName);
    }

    public async Task<Member?> GetByIdAsync(string tenantId, string id)
    {
        try
        {
            var response = await _container.ReadItemAsync<Member>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Member?> GetByMemberIdAsync(string tenantId, string memberId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.memberId = @memberId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId);

        var iterator = _container.GetItemQueryIterator<Member>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId)
        });

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            return response.FirstOrDefault();
        }

        return null;
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
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var parameters = new List<(string Name, object Value)> { ("@tenantId", tenantId) };

        if (!string.IsNullOrEmpty(groupNumber))
        {
            queryText += " AND c.groupNumber = @groupNumber";
            parameters.Add(("@groupNumber", groupNumber));
        }

        if (!string.IsNullOrEmpty(lastName))
        {
            queryText += " AND CONTAINS(LOWER(c.lastName), LOWER(@lastName))";
            parameters.Add(("@lastName", lastName));
        }

        if (dateOfBirth.HasValue)
        {
            queryText += " AND c.dateOfBirth = @dateOfBirth";
            parameters.Add(("@dateOfBirth", dateOfBirth.Value));
        }

        if (activeOnly)
        {
            queryText += " AND c.status = @activeStatus";
            parameters.Add(("@activeStatus", (int)EnrollmentStatus.Active));
        }

        if (subscribersOnly)
        {
            queryText += " AND c.isSubscriber = true";
        }

        var queryDef = new QueryDefinition(queryText);
        foreach (var (name, value) in parameters)
        {
            queryDef.WithParameter(name, value);
        }

        var iterator = _container.GetItemQueryIterator<Member>(
            queryDef,
            continuationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId),
                MaxItemCount = pageSize
            });

        var results = new List<Member>();
        string? newContinuationToken = null;

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
            newContinuationToken = response.ContinuationToken;
        }

        return (results, newContinuationToken);
    }

    public async Task<List<Member>> GetDependentsAsync(string tenantId, string subscriberMemberId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.subscriberMemberId = @subscriberId AND c.isSubscriber = false")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@subscriberId", subscriberMemberId);

        var iterator = _container.GetItemQueryIterator<Member>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId)
        });

        var dependents = new List<Member>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            dependents.AddRange(response);
        }

        return dependents;
    }

    public async Task<int> GetCountByGroupAsync(string tenantId, string groupNumber)
    {
        var query = new QueryDefinition(
            "SELECT VALUE COUNT(1) FROM c WHERE c.tenantId = @tenantId AND c.groupNumber = @groupNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@groupNumber", groupNumber);

        var iterator = _container.GetItemQueryIterator<int>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId)
        });

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            return response.FirstOrDefault();
        }

        return 0;
    }

    public async Task<Member> CreateAsync(Member member)
    {
        member.CreatedDate = DateTime.UtcNow;
        member.LastUpdatedDate = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(
            member,
            new PartitionKey(member.TenantId));

        return response.Resource;
    }

    public async Task<Member> UpdateAsync(Member member)
    {
        member.LastUpdatedDate = DateTime.UtcNow;

        var response = await _container.ReplaceItemAsync(
            member,
            member.Id,
            new PartitionKey(member.TenantId));

        return response.Resource;
    }

    public async Task DeleteAsync(string tenantId, string id)
    {
        await _container.DeleteItemAsync<Member>(
            id,
            new PartitionKey(tenantId));
    }

    public async Task<bool> ExistsAsync(string tenantId, string memberId)
    {
        var member = await GetByMemberIdAsync(tenantId, memberId);
        return member != null;
    }

    public async Task<Member?> GetByIdentifierAsync(string tenantId, string system, string value)
    {
        var query = new QueryDefinition(@"
            SELECT TOP 1 * FROM c
            WHERE c.tenantId = @tenantId
              AND EXISTS(
                SELECT VALUE i FROM i IN c.identifiers
                WHERE i.system = @system AND i.value = @value
              )")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@system", system)
            .WithParameter("@value", value);

        var iterator = _container.GetItemQueryIterator<Member>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId)
        });

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            return response.FirstOrDefault();
        }
        return null;
    }
}

/// <summary>
/// Repository interface for Member entities
/// </summary>
public interface IMemberRepository
{
    Task<Member?> GetByIdAsync(string tenantId, string id);
    Task<Member?> GetByMemberIdAsync(string tenantId, string memberId);
    Task<(IEnumerable<Member> Items, string? ContinuationToken)> SearchAsync(
        string tenantId,
        string? groupNumber = null,
        string? lastName = null,
        DateTime? dateOfBirth = null,
        bool activeOnly = false,
        bool subscribersOnly = false,
        int pageSize = 20,
        string? continuationToken = null);
    Task<List<Member>> GetDependentsAsync(string tenantId, string subscriberMemberId);
    Task<int> GetCountByGroupAsync(string tenantId, string groupNumber);
    Task<Member> CreateAsync(Member member);
    Task<Member> UpdateAsync(Member member);
    Task DeleteAsync(string tenantId, string id);
    Task<bool> ExistsAsync(string tenantId, string memberId);

    /// <summary>
    /// Find a member by typed identifier (system + value). Used for idempotent member
    /// creation and portal lookups.
    /// </summary>
    Task<Member?> GetByIdentifierAsync(string tenantId, string system, string value);
}
