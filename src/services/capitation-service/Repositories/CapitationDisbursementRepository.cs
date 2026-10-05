using Microsoft.Azure.Cosmos;
using CapitationService.Models;

namespace CapitationService.Repositories;

public interface ICapitationDisbursementRepository
{
    Task<CapitationDisbursement?> GetByIdAsync(string id);
    Task<IEnumerable<CapitationDisbursement>> GetByStatementIdAsync(string statementId);
    Task<IEnumerable<CapitationDisbursement>> GetByStatusAsync(DisbursementStatus status);
    Task<IEnumerable<CapitationDisbursement>> GetByStripeTransferIdAsync(string transferId);
    Task<CapitationDisbursement> CreateAsync(CapitationDisbursement disbursement);
    Task<CapitationDisbursement> UpdateAsync(CapitationDisbursement disbursement);

    /// <summary>
    /// Pending to <see cref="DisbursementStatus.Releasing"/> under <paramref name="claimId"/>,
    /// as one conditional write: of two releases racing for the same disbursement
    /// exactly one gets it. False when it is no longer Pending.
    /// </summary>
    Task<bool> TryClaimForReleaseAsync(string id, string claimId, DateTime claimedAt, string releasedBy);

    /// <summary>
    /// Releasing under <paramref name="claimId"/> back to Pending (nothing was sent),
    /// recording why. Does nothing when it is not held by that claim.
    /// </summary>
    Task ReleaseClaimAsync(string id, string claimId, string? reason);
}

/// <summary>
/// Cosmos DB implementation of capitation disbursement repository
/// </summary>
public class CapitationDisbursementRepository : ICapitationDisbursementRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CapitationDisbursementRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = cosmosClient.GetContainer(databaseName, "CapitationDisbursements");
        _httpContextAccessor = httpContextAccessor;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
    }

    public async Task<CapitationDisbursement?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        try
        {
            var response = await _container.ReadItemAsync<CapitationDisbursement>(id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> TryClaimForReleaseAsync(string id, string claimId, DateTime claimedAt, string releasedBy)
    {
        var tenantId = GetTenantId();
        ItemResponse<CapitationDisbursement> current;
        try
        {
            current = await _container.ReadItemAsync<CapitationDisbursement>(id, new PartitionKey(tenantId));
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        var disbursement = current.Resource;
        if (disbursement.Status != DisbursementStatus.Pending)
            return false;

        disbursement.Status = DisbursementStatus.Releasing;
        disbursement.ReleaseClaimId = claimId;
        disbursement.ReleaseClaimedAt = claimedAt;
        disbursement.ReleasedBy = releasedBy;
        disbursement.LastUpdatedAt = DateTime.UtcNow;
        try
        {
            // Optimistic concurrency: the replace applies only to the version read
            // above, so of two releases exactly one moves it to Releasing.
            await _container.ReplaceItemAsync(disbursement, id, new PartitionKey(tenantId),
                new ItemRequestOptions { IfMatchEtag = current.ETag });
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task ReleaseClaimAsync(string id, string claimId, string? reason)
    {
        var tenantId = GetTenantId();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ItemResponse<CapitationDisbursement> current;
            try
            {
                current = await _container.ReadItemAsync<CapitationDisbursement>(id, new PartitionKey(tenantId));
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return;
            }

            var disbursement = current.Resource;
            if (disbursement.Status != DisbursementStatus.Releasing || disbursement.ReleaseClaimId != claimId)
                return;

            disbursement.Status = DisbursementStatus.Pending;
            disbursement.ReleaseClaimId = null;
            disbursement.ReleaseClaimedAt = null;
            disbursement.ReleasedBy = null;
            disbursement.ErrorMessage = reason;
            disbursement.LastUpdatedAt = DateTime.UtcNow;
            try
            {
                await _container.ReplaceItemAsync(disbursement, id, new PartitionKey(tenantId),
                    new ItemRequestOptions { IfMatchEtag = current.ETag });
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                // Changed since the read: read again.
            }
        }
    }

    public async Task<IEnumerable<CapitationDisbursement>> GetByStatementIdAsync(string statementId)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.statementId = @statementId ORDER BY c.createdAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@statementId", statementId);
        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<CapitationDisbursement>> GetByStatusAsync(DisbursementStatus status)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status = @status ORDER BY c.createdAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@status", status.ToString());
        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<CapitationDisbursement>> GetByStripeTransferIdAsync(string transferId)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.stripeTransferId = @transferId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@transferId", transferId);
        return await ExecuteQueryAsync(query);
    }

    public async Task<CapitationDisbursement> CreateAsync(CapitationDisbursement disbursement)
    {
        disbursement.TenantId = GetTenantId();
        var response = await _container.CreateItemAsync(disbursement, new PartitionKey(disbursement.TenantId));
        return response.Resource;
    }

    public async Task<CapitationDisbursement> UpdateAsync(CapitationDisbursement disbursement)
    {
        disbursement.LastUpdatedAt = DateTime.UtcNow;
        var response = await _container.UpsertItemAsync(disbursement, new PartitionKey(disbursement.TenantId));
        return response.Resource;
    }

    private async Task<List<CapitationDisbursement>> ExecuteQueryAsync(QueryDefinition query)
    {
        var results = new List<CapitationDisbursement>();
        using var iterator = _container.GetItemQueryIterator<CapitationDisbursement>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }
}
