using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public interface IEftDraftRepository
{
    /// <summary>
    /// Creates the draft. An active draft (<see cref="EftDraft.IsActive"/>) takes
    /// its invoice: <see cref="InvoiceDraftConflictException"/> when the invoice
    /// already has an active draft, and nothing is written.
    /// </summary>
    Task<EftDraft> CreateAsync(EftDraft draft);

    Task<EftDraft?> GetByIdAsync(string id);

    /// <summary>Replaces the draft; one that is no longer active frees its invoice.</summary>
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
    Task<bool> TryClaimForReleaseAsync(string id, string claimId, DateTime claimedAt, string releasedBy);

    /// <summary>
    /// Releasing under <paramref name="claimId"/> back to Pending (nothing was sent),
    /// recording why. Does nothing when the draft is not held by that claim.
    /// </summary>
    Task ReleaseClaimAsync(string id, string claimId, string? reason);

    /// <summary>
    /// Pending to Cancelled as one conditional write, freeing the invoice for a
    /// new draft. False when the draft is no longer Pending.
    /// </summary>
    Task<bool> TryCancelPendingAsync(string id, string cancelledBy);
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
        draft.RefreshActiveInvoiceKey();
        var partitionKey = new PartitionKey(draft.TenantId);
        if (draft.ActiveInvoiceKey == null)
            return (await _container.CreateItemAsync(draft, partitionKey)).Resource;

        // An active draft from before the lock items existed holds its invoice too.
        if ((await GetByInvoiceIdAsync(draft.InvoiceId)).Any(d => EftDraft.IsActive(d.Status)))
            throw new InvoiceDraftConflictException(draft.InvoiceId);

        // The lock item and the draft in one transaction: the lock's id is the
        // invoice, so of two drafts of one invoice exactly one is created.
        var lockItem = InvoiceDraftLock.For(draft);
        using var batch = await _container.CreateTransactionalBatch(partitionKey)
            .CreateItem(lockItem)
            .CreateItem(draft)
            .ExecuteAsync();
        if (batch.IsSuccessStatusCode)
            return draft;
        if (batch.Count > 0 && batch[0].StatusCode == HttpStatusCode.Conflict)
            throw new InvoiceDraftConflictException(draft.InvoiceId);
        throw new InvalidOperationException($"EFT draft for invoice {draft.InvoiceId} was not created ({(int)batch.StatusCode} {batch.ErrorMessage}).");
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
        draft.RefreshActiveInvoiceKey();
        var partitionKey = new PartitionKey(draft.TenantId);
        if (draft.ActiveInvoiceKey == null)
        {
            // No longer active: the draft and the release of its invoice lock in one transaction.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var held = await ReadLockAsync(draft.TenantId, draft.InvoiceId);
                if (held == null || held.Value.Lock.DraftId != draft.Id)
                    break;
                using var batch = await _container.CreateTransactionalBatch(partitionKey)
                    .UpsertItem(draft)
                    .DeleteItem(held.Value.Lock.Id, new TransactionalBatchItemRequestOptions { IfMatchEtag = held.Value.ETag })
                    .ExecuteAsync();
                if (batch.IsSuccessStatusCode)
                    return draft;
            }
        }
        var response = await _container.UpsertItemAsync(draft, partitionKey);
        return response.Resource;
    }

    public async Task<bool> TryCancelPendingAsync(string id, string cancelledBy)
    {
        var tenantId = GetTenantId();
        var partitionKey = new PartitionKey(tenantId);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ItemResponse<EftDraft> current;
            try
            {
                current = await _container.ReadItemAsync<EftDraft>(id, partitionKey);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            var draft = current.Resource;
            if (draft.Status != EftDraftStatus.Pending)
                return false;
            draft.Status = EftDraftStatus.Cancelled;
            draft.LastUpdatedBy = cancelledBy;
            draft.LastUpdatedAt = DateTime.UtcNow;
            draft.RefreshActiveInvoiceKey();

            // Pending to Cancelled only for the version read (ETag), with the release of the invoice lock.
            var batch = _container.CreateTransactionalBatch(partitionKey)
                .ReplaceItem(id, draft, new TransactionalBatchItemRequestOptions { IfMatchEtag = current.ETag });
            var held = await ReadLockAsync(tenantId, draft.InvoiceId);
            if (held != null && held.Value.Lock.DraftId == id)
                batch = batch.DeleteItem(held.Value.Lock.Id, new TransactionalBatchItemRequestOptions { IfMatchEtag = held.Value.ETag });
            using var response = await batch.ExecuteAsync();
            if (response.IsSuccessStatusCode)
                return true;
            // Changed since the read: read again.
        }
        return false;
    }

    private async Task<(InvoiceDraftLock Lock, string ETag)?> ReadLockAsync(string tenantId, string invoiceId)
    {
        try
        {
            var response = await _container.ReadItemAsync<InvoiceDraftLock>(InvoiceDraftLock.IdFor(invoiceId), new PartitionKey(tenantId));
            return (response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
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

    public async Task<bool> TryClaimForReleaseAsync(string id, string claimId, DateTime claimedAt, string releasedBy)
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
        draft.ReleasedBy = releasedBy;
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
            draft.ReleasedBy = null;
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

/// <summary>
/// Cosmos item (in the EftDrafts container, the tenant's partition) whose id is
/// the invoice: it exists while the invoice has an active draft. Created with
/// the draft and deleted when the draft is no longer active, each in one
/// transactional batch. Carries no status or invoiceId, so draft queries never
/// return it.
/// </summary>
public sealed class InvoiceDraftLock
{
    public const string DocType = "invoiceDraftLock";

    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("docType")] public string Type { get; set; } = DocType;
    [JsonPropertyName("lockedInvoiceId")] public string LockedInvoiceId { get; set; } = string.Empty;
    [JsonPropertyName("draftId")] public string DraftId { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public static string IdFor(string invoiceId) => $"invoice-draft-lock:{invoiceId}";

    public static InvoiceDraftLock For(EftDraft draft) => new()
    {
        Id = IdFor(draft.InvoiceId),
        TenantId = draft.TenantId,
        LockedInvoiceId = draft.InvoiceId,
        DraftId = draft.Id,
    };
}
