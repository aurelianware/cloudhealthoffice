using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemberService.Models;
using Microsoft.Azure.Cosmos;

namespace MemberService.Repositories;

/// <summary>
/// Cosmos DB repository for <see cref="FamilyRelationship"/>. Uses <c>TransactionalBatch</c>
/// for symmetric-pair writes — both rows share <c>tenantId</c> so they live in the same
/// partition, which is a precondition for batch atomicity in Cosmos.
/// </summary>
public class FamilyRelationshipRepository : IFamilyRelationshipRepository
{
    private readonly Container _container;
    public const string ContainerName = "FamilyRelationships";
    public const string PartitionKeyPath = "/tenantId";

    public FamilyRelationshipRepository(CosmosClient cosmosClient, string databaseName)
    {
        var database = cosmosClient.GetDatabase(databaseName);
        _container = database.GetContainer(ContainerName);
    }

    public async Task CreatePairAsync(FamilyRelationship forward, FamilyRelationship inverse, CancellationToken ct = default)
    {
        EnsureSameTenant(forward, inverse);
        var batch = _container.CreateTransactionalBatch(new PartitionKey(forward.TenantId))
            .CreateItem(forward)
            .CreateItem(inverse);

        using var response = await batch.ExecuteAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new CosmosException(
                $"Symmetric-pair create failed (pairId={forward.PairId}): {response.ErrorMessage}",
                response.StatusCode, 0, response.ActivityId, response.RequestCharge);
        }
    }

    public async Task UpdatePairAsync(FamilyRelationship forward, FamilyRelationship inverse, CancellationToken ct = default)
    {
        EnsureSameTenant(forward, inverse);
        var batch = _container.CreateTransactionalBatch(new PartitionKey(forward.TenantId))
            .ReplaceItem(forward.Id, forward)
            .ReplaceItem(inverse.Id, inverse);

        using var response = await batch.ExecuteAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new CosmosException(
                $"Symmetric-pair update failed (pairId={forward.PairId}): {response.ErrorMessage}",
                response.StatusCode, 0, response.ActivityId, response.RequestCharge);
        }
    }

    public async Task<FamilyRelationship?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<FamilyRelationship>(
                id, new PartitionKey(tenantId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<List<FamilyRelationship>> GetPairAsync(string tenantId, string pairId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @t AND c.pairId = @p")
            .WithParameter("@t", tenantId)
            .WithParameter("@p", pairId);

        return await QueryAllAsync(query, tenantId, ct);
    }

    public async Task<List<FamilyRelationship>> ListBySubjectAsync(
        string tenantId, string subjectMemberId, bool includeDeleted = false, CancellationToken ct = default)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @t AND c.subjectMemberId = @s";
        // System.Text.Json emits missing-nullable as `null`, not as an absent property,
        // so a strict `NOT IS_DEFINED` filter would hide every non-deleted row. Allow
        // both cases: the property is missing OR explicitly null.
        if (!includeDeleted) queryText += " AND (NOT IS_DEFINED(c.deletedAt) OR c.deletedAt = null)";
        var query = new QueryDefinition(queryText)
            .WithParameter("@t", tenantId)
            .WithParameter("@s", subjectMemberId);

        return await QueryAllAsync(query, tenantId, ct);
    }

    public async Task<List<FamilyRelationship>> ListTouchingAsync(
        string tenantId, string memberId, bool includeDeleted = false, CancellationToken ct = default)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @t AND (c.subjectMemberId = @m OR c.relatedMemberId = @m)";
        // System.Text.Json emits missing-nullable as `null`, not as an absent property,
        // so a strict `NOT IS_DEFINED` filter would hide every non-deleted row. Allow
        // both cases: the property is missing OR explicitly null.
        if (!includeDeleted) queryText += " AND (NOT IS_DEFINED(c.deletedAt) OR c.deletedAt = null)";
        var query = new QueryDefinition(queryText)
            .WithParameter("@t", tenantId)
            .WithParameter("@m", memberId);

        return await QueryAllAsync(query, tenantId, ct);
    }

    public async Task<FamilyRelationship?> FindActivePairAsync(
        string tenantId, string subjectMemberId, string relatedMemberId, CancellationToken ct = default)
    {
        // Active = not soft-deleted AND (no end date OR end date is in the future).
        // Matches FamilyRelationship.IsActive and DeriveSubscriberMemberIdAsync —
        // an EndDate in the future must still block duplicate creates.
        var query = new QueryDefinition(@"
                SELECT TOP 1 * FROM c
                WHERE c.tenantId = @t
                  AND c.subjectMemberId = @s
                  AND c.relatedMemberId = @r
                  AND (NOT IS_DEFINED(c.deletedAt) OR c.deletedAt = null)
                  AND (NOT IS_DEFINED(c.endDate) OR c.endDate = null OR c.endDate > @now)")
            .WithParameter("@t", tenantId)
            .WithParameter("@s", subjectMemberId)
            .WithParameter("@r", relatedMemberId)
            .WithParameter("@now", DateTime.UtcNow);

        var results = await QueryAllAsync(query, tenantId, ct);
        return results.FirstOrDefault();
    }

    private async Task<List<FamilyRelationship>> QueryAllAsync(QueryDefinition query, string tenantId, CancellationToken ct)
    {
        var iterator = _container.GetItemQueryIterator<FamilyRelationship>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<FamilyRelationship>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    private static void EnsureSameTenant(FamilyRelationship a, FamilyRelationship b)
    {
        if (!string.Equals(a.TenantId, b.TenantId, StringComparison.Ordinal))
        {
            // Guard against a coding mistake in the service layer. Symmetric-pair
            // atomicity relies on both rows sharing the same Cosmos partition key.
            throw new InvalidOperationException(
                "FamilyRelationship pair rows must share the same TenantId. " +
                "Cross-tenant relationships are not supported in this phase.");
        }
    }
}
