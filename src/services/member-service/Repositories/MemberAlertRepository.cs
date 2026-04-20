using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MemberService.Models;
using Microsoft.Azure.Cosmos;

namespace MemberService.Repositories;

/// <summary>
/// Cosmos DB repository for <see cref="MemberAlert"/>. Partition key is
/// <c>/tenantId</c> for multi-tenant isolation. Alerts are never hard-deleted —
/// the lifecycle is end-dating via <see cref="EndAsync"/>.
/// </summary>
public class MemberAlertRepository : IMemberAlertRepository
{
    private readonly Container _container;
    private const string ContainerName = "MemberAlerts";

    public MemberAlertRepository(CosmosClient cosmosClient, string databaseName)
    {
        _container = cosmosClient.GetDatabase(databaseName).GetContainer(ContainerName);
    }

    public async Task<MemberAlert> CreateAsync(MemberAlert alert)
    {
        if (string.IsNullOrEmpty(alert.Id)) alert.Id = Guid.NewGuid().ToString();
        if (alert.CreatedDate == default) alert.CreatedDate = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(alert, new PartitionKey(alert.TenantId));
        return response.Resource;
    }

    public async Task<MemberAlert?> GetByIdAsync(string tenantId, string memberId, string alertId)
    {
        try
        {
            var response = await _container.ReadItemAsync<MemberAlert>(alertId, new PartitionKey(tenantId));
            var alert = response.Resource;
            return alert.MemberId == memberId ? alert : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<MemberAlert>> ListByMemberAsync(
        string tenantId,
        string memberId,
        bool activeOnly,
        DateTime? asOf = null)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.memberId = @memberId";
        var query = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId);

        var iterator = _container.GetItemQueryIterator<MemberAlert>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId)
        });

        var results = new List<MemberAlert>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            results.AddRange(page);
        }

        // The "active" predicate is computed in-process so callers and tests
        // share the same definition (see MemberAlert.IsActive).
        var t = asOf ?? DateTime.UtcNow;
        if (activeOnly) results = results.Where(a => a.IsActive(t)).ToList();

        // Stable order: most-recent start first, then by alert type for determinism.
        return results
            .OrderByDescending(a => a.StartDate)
            .ThenBy(a => a.AlertType)
            .ToList();
    }

    public async Task<MemberAlert> EndAsync(MemberAlert alert)
    {
        var response = await _container.ReplaceItemAsync(alert, alert.Id, new PartitionKey(alert.TenantId));
        return response.Resource;
    }
}

public interface IMemberAlertRepository
{
    Task<MemberAlert> CreateAsync(MemberAlert alert);
    Task<MemberAlert?> GetByIdAsync(string tenantId, string memberId, string alertId);

    /// <summary>
    /// List alerts for a member. When <paramref name="activeOnly"/> is true, only
    /// alerts whose effective window contains <paramref name="asOf"/> (default = now)
    /// are returned.
    /// </summary>
    Task<IReadOnlyList<MemberAlert>> ListByMemberAsync(
        string tenantId,
        string memberId,
        bool activeOnly,
        DateTime? asOf = null);

    /// <summary>Persist an end-dated alert. Caller must set <see cref="MemberAlert.EndDate"/> and <see cref="MemberAlert.EndedBy"/>.</summary>
    Task<MemberAlert> EndAsync(MemberAlert alert);
}
