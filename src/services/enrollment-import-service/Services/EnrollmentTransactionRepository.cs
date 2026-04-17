using EnrollmentImportService.Models;
using Microsoft.Azure.Cosmos;

namespace EnrollmentImportService.Services;

public interface IEnrollmentTransactionRepository
{
    Task<EnrollmentTransaction> CreateAsync(EnrollmentTransaction txn);
    Task<IReadOnlyList<EnrollmentTransaction>> ListByMemberAsync(
        string tenantId,
        string memberId,
        int limit = 100);
}

/// <summary>
/// Cosmos DB repository for individual 834 transaction records. Partition key
/// is <c>/tenantId</c>, consistent with the Members container.
/// </summary>
public class EnrollmentTransactionRepository : IEnrollmentTransactionRepository
{
    private readonly CosmosClient _cosmosClient;
    private readonly IConfiguration _config;

    public EnrollmentTransactionRepository(CosmosClient cosmosClient, IConfiguration config)
    {
        _cosmosClient = cosmosClient;
        _config = config;
    }

    private Container TransactionsContainer => _cosmosClient.GetContainer(
        _config["CosmosDb:DatabaseName"] ?? "CloudHealthOffice",
        _config["CosmosDb:TransactionsContainerName"] ?? "enrollment-transactions");

    public async Task<EnrollmentTransaction> CreateAsync(EnrollmentTransaction txn)
    {
        if (string.IsNullOrEmpty(txn.Id)) txn.Id = Guid.NewGuid().ToString();
        var response = await TransactionsContainer.CreateItemAsync(
            txn, new PartitionKey(txn.TenantId));
        return response.Resource;
    }

    public async Task<IReadOnlyList<EnrollmentTransaction>> ListByMemberAsync(
        string tenantId, string memberId, int limit = 100)
    {
        var query = new QueryDefinition(
            "SELECT TOP @limit * FROM c WHERE c.tenantId = @t AND c.memberId = @m ORDER BY c.receivedAt DESC")
            .WithParameter("@t", tenantId)
            .WithParameter("@m", memberId)
            .WithParameter("@limit", limit);

        var iterator = TransactionsContainer.GetItemQueryIterator<EnrollmentTransaction>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<EnrollmentTransaction>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            results.AddRange(page);
        }
        return results;
    }
}
