using Microsoft.Azure.Cosmos;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public interface IEftDraftRepository
{
    Task<EftDraft> CreateAsync(EftDraft draft);
    Task<EftDraft?> GetByIdAsync(string id);
    Task<EftDraft> UpdateAsync(EftDraft draft);
    Task<IEnumerable<EftDraft>> GetByInvoiceIdAsync(string invoiceId);
    Task<IEnumerable<EftDraft>> GetByStatusAsync(EftDraftStatus status);
    Task<IEnumerable<EftDraft>> GetByStripePaymentIntentIdAsync(string paymentIntentId);
    Task<IEnumerable<EftDraft>> GetPendingDraftsAsync();

    /// <summary>
    /// Pending to <see cref="EftDraftStatus.Releasing"/> under <paramref name="claimId"/>,
    /// as one conditional write: of two releases racing for the same draft exactly
    /// one gets it. False when the draft is no longer Pending.
    /// </summary>
    Task<bool> TryClaimForReleaseAsync(string id, string claimId, DateTime claimedAt);

    /// <summary>
    /// Releasing under <paramref name="claimId"/> back to Pending (nothing was sent),
    /// recording why. Does nothing when the draft is not held by that claim.
    /// </summary>
    Task ReleaseClaimAsync(string id, string claimId, string? reason);
}

/// <summary>
/// Cosmos DB implementation of EFT draft repository
/// </summary>
public class EftDraftRepository : IEftDraftRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public EftDraftRepository(CosmosClient cosmosClient, IConfiguration configuration, IHttpContextAccessor httpContextAccessor)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = cosmosClient.GetContainer(databaseName, "EftDrafts");
        _httpContextAccessor = httpContextAccessor;
    }

    private string GetTenantId() =>
        _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenantId
            ? tenantId
            : throw new InvalidOperationException("TenantId not found in request context");

    public async Task<EftDraft> CreateAsync(EftDraft draft)
    {
        draft.TenantId = GetTenantId();
        var response = await _container.CreateItemAsync(draft, new PartitionKey(draft.TenantId));
        return response.Resource;
    }

    public async Task<EftDraft?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        try
        {
            var response = await _container.ReadItemAsync<EftDraft>(id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<EftDraft> UpdateAsync(EftDraft draft)
    {
        draft.LastUpdatedAt = DateTime.UtcNow;
        var response = await _container.UpsertItemAsync(draft, new PartitionKey(draft.TenantId));
        return response.Resource;
    }

    public async Task<IEnumerable<EftDraft>> GetByInvoiceIdAsync(string invoiceId)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenantId AND c.invoiceId = @invoiceId ORDER BY c.createdAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@invoiceId", invoiceId);
        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<EftDraft>> GetByStatusAsync(EftDraftStatus status)
    {
        var tenantId = GetTenantId();
        var statusStr = status.ToString();
        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status = @status ORDER BY c.createdAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@status", statusStr);
        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<EftDraft>> GetByStripePaymentIntentIdAsync(string paymentIntentId)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenantId AND c.stripePaymentIntentId = @piId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@piId", paymentIntentId);
        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<EftDraft>> GetPendingDraftsAsync()
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenantId AND (c.status = 'Pending' OR c.status = 'Submitted') ORDER BY c.createdAt ASC")
            .WithParameter("@tenantId", tenantId);
        return await ExecuteQueryAsync(query);
    }

    public async Task<bool> TryClaimForReleaseAsync(string id, string claimId, DateTime claimedAt)
    {
        var tenantId = GetTenantId();
        ItemResponse<EftDraft> current;
        try
        {
            current = await _container.ReadItemAsync<EftDraft>(id, new PartitionKey(tenantId));
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        var draft = current.Resource;
        if (draft.Status != EftDraftStatus.Pending)
            return false;

        draft.Status = EftDraftStatus.Releasing;
        draft.ReleaseClaimId = claimId;
        draft.ReleaseClaimedAt = claimedAt;
        draft.LastUpdatedAt = DateTime.UtcNow;
        try
        {
            // Optimistic concurrency: the replace applies only to the version read
            // above, so of two releases exactly one moves it to Releasing.
            await _container.ReplaceItemAsync(draft, id, new PartitionKey(tenantId),
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
            ItemResponse<EftDraft> current;
            try
            {
                current = await _container.ReadItemAsync<EftDraft>(id, new PartitionKey(tenantId));
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return;
            }

            var draft = current.Resource;
            if (draft.Status != EftDraftStatus.Releasing || draft.ReleaseClaimId != claimId)
                return;

            draft.Status = EftDraftStatus.Pending;
            draft.ReleaseClaimId = null;
            draft.ReleaseClaimedAt = null;
            draft.ErrorMessage = reason;
            draft.LastUpdatedAt = DateTime.UtcNow;
            try
            {
                await _container.ReplaceItemAsync(draft, id, new PartitionKey(tenantId),
                    new ItemRequestOptions { IfMatchEtag = current.ETag });
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                // Changed since the read: read again.
            }
        }
    }

    private async Task<List<EftDraft>> ExecuteQueryAsync(QueryDefinition query)
    {
        var results = new List<EftDraft>();
        using var iterator = _container.GetItemQueryIterator<EftDraft>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }
}
