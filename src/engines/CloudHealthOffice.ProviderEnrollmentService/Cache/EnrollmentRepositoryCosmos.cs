using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ProviderEnrollmentService.Cache;

/// <summary>
/// Cosmos DB implementation of IEnrollmentRepository.
///
/// Container layout:
///   enrollment-cache    — partition key: /stateCode
///
/// All records use document-level TTL (driven by ProviderEnrollmentOptions.CacheTtl).
/// The container must have DefaultTimeToLive = -1 to honor per-document TTL.
/// </summary>
public sealed class EnrollmentRepositoryCosmos : IEnrollmentRepository
{
    private readonly Container _container;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<EnrollmentRepositoryCosmos> _logger;

    public EnrollmentRepositoryCosmos(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IOptions<ProviderEnrollmentOptions> options,
        ILogger<EnrollmentRepositoryCosmos> logger)
    {
        var databaseName  = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["ProviderEnrollmentService:CacheContainer"] ?? "enrollment-cache";

        _container = cosmosClient.GetContainer(databaseName, containerName);
        _cacheTtl  = options.Value.CacheTtl;
        _logger    = logger;
    }

    // ── IEnrollmentRepository ─────────────────────────────────────

    public async Task<StateEnrollmentRecord?> GetAsync(
        string npi, string stateCode, CancellationToken ct = default)
    {
        try
        {
            var id = EnrollmentCacheDocument.MakeId(npi, stateCode);
            var response = await _container.ReadItemAsync<EnrollmentCacheDocument>(
                id, new PartitionKey(stateCode), cancellationToken: ct);
            return response.Resource.ToRecord();
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<StateEnrollmentRecord>> GetAllStatesAsync(
        string npi, CancellationToken ct = default)
    {
        // Cross-partition query — acceptable here because this is a low-frequency
        // operation (provider profile page) rather than a high-throughput path.
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.npi = @npi")
            .WithParameter("@npi", npi);

        var iterator = _container.GetItemQueryIterator<EnrollmentCacheDocument>(
            query,
            requestOptions: new QueryRequestOptions { MaxItemCount = 50 });

        var results = new List<StateEnrollmentRecord>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page.Select(d => d.ToRecord()));
        }

        return results;
    }

    public async Task UpsertAsync(StateEnrollmentRecord record, CancellationToken ct = default)
    {
        var doc = EnrollmentCacheDocument.FromRecord(record, _cacheTtl);
        await _container.UpsertItemAsync(
            doc,
            new PartitionKey(record.StateCode),
            cancellationToken: ct);
    }

    public async Task BulkUpsertAsync(
        IEnumerable<StateEnrollmentRecord> records, CancellationToken ct = default)
    {
        // Cosmos SDK bulk execution — enable via CosmosClientOptions.AllowBulkExecution = true
        var tasks = records.Select(r =>
        {
            var doc = EnrollmentCacheDocument.FromRecord(r, _cacheTtl);
            return _container.UpsertItemAsync(
                doc,
                new PartitionKey(r.StateCode),
                cancellationToken: ct);
        });

        var results = await Task.WhenAll(tasks.Select(async t =>
        {
            try { await t; return true; }
            catch (CosmosException ex)
            {
                _logger.LogWarning(ex, "Bulk upsert failed for one record");
                return false;
            }
        }));

        var failed = results.Count(r => !r);
        if (failed > 0)
            _logger.LogWarning("{Failed} records failed during bulk upsert", failed);
    }

    public async Task<IReadOnlyList<StateEnrollmentRecord>> GetProvidersWithRevalidationDueSoonAsync(
        int withinDays, string? stateCode = null, CancellationToken ct = default)
    {
        var today   = DateOnly.FromDateTime(DateTime.UtcNow);
        var horizon = today.AddDays(withinDays).ToString("O");
        var todayStr = today.ToString("O");

        var sql = stateCode is not null
            ? "SELECT * FROM c WHERE c.stateCode = @stateCode " +
              "AND c.revalidationDueDate >= @today AND c.revalidationDueDate <= @horizon " +
              "AND c.status = 'Active'"
            : "SELECT * FROM c " +
              "WHERE c.revalidationDueDate >= @today AND c.revalidationDueDate <= @horizon " +
              "AND c.status = 'Active'";

        var query = new QueryDefinition(sql)
            .WithParameter("@today",   todayStr)
            .WithParameter("@horizon", horizon);

        if (stateCode is not null)
            query = query.WithParameter("@stateCode", stateCode);

        var queryOptions = stateCode is not null
            ? new QueryRequestOptions { PartitionKey = new PartitionKey(stateCode) }
            : new QueryRequestOptions();

        var iterator = _container.GetItemQueryIterator<EnrollmentCacheDocument>(query, requestOptions: queryOptions);
        var results = new List<StateEnrollmentRecord>();

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page.Select(d => d.ToRecord()));
        }

        return results;
    }

    public async Task<IReadOnlyList<StateEnrollmentRecord>> GetActivePanelByMcoAsync(
        string stateCode, string mcoId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c " +
            "WHERE c.stateCode = @stateCode " +
            "AND c.status = 'Active' " +
            "AND ARRAY_CONTAINS(c.mcoParticipation, @mcoId)")
            .WithParameter("@stateCode", stateCode)
            .WithParameter("@mcoId",     mcoId);

        var iterator = _container.GetItemQueryIterator<EnrollmentCacheDocument>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(stateCode) });

        var results = new List<StateEnrollmentRecord>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page.Select(d => d.ToRecord()));
        }

        return results;
    }

    public async Task DeleteAsync(string npi, string stateCode, CancellationToken ct = default)
    {
        try
        {
            var id = EnrollmentCacheDocument.MakeId(npi, stateCode);
            await _container.DeleteItemAsync<EnrollmentCacheDocument>(
                id, new PartitionKey(stateCode), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone — idempotent delete is fine
        }
    }
}
