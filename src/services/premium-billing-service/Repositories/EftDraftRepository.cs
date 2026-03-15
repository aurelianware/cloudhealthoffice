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
        _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString() ?? "default";

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
