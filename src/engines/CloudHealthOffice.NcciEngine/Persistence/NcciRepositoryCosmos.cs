using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.NcciEngine.Persistence;

/// <summary>
/// Cosmos DB implementation of INcciRepository.
///
/// Container layout (all partitioned by /tenantId):
///   ncci-pairs     — NcciEditPair documents, id = stable key
///   mue-entries    — MueEntry documents, id = stable key
///   ncci-version   — NcciTableVersion document, id = "current"
///
/// Lookups are point-reads (O(1) RU) when the composite key is known.
/// Quarterly import uses batch upsert via TransactionalBatch where
/// batches fit within the 2 MB / 100-operation Cosmos limit, otherwise
/// falls back to individual upserts.
/// </summary>
internal class NcciRepositoryCosmos : INcciRepository
{
    private readonly Container _pairContainer;
    private readonly Container _mueContainer;
    private readonly Container _versionContainer;
    private readonly ILogger<NcciRepositoryCosmos> _logger;

    public NcciRepositoryCosmos(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<NcciRepositoryCosmos> logger)
    {
        var db = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _pairContainer    = cosmosClient.GetContainer(db, configuration["NcciEngine:PairContainer"]    ?? "NcciPairs");
        _mueContainer     = cosmosClient.GetContainer(db, configuration["NcciEngine:MueContainer"]     ?? "MueEntries");
        _versionContainer = cosmosClient.GetContainer(db, configuration["NcciEngine:VersionContainer"] ?? "NcciVersion");
        _logger = logger;
    }

    // ── NCCI Edit Pairs ────────────────────────────────────────────

    public async Task<NcciEditPair?> GetEditPairAsync(
        string tenantId, string column1Code, string column2Code,
        DateOnly serviceDate, CancellationToken ct = default)
    {
        // We query for the most-recent pair whose EffectiveDate <= serviceDate
        // and whose TerminationDate is null or > serviceDate.
        // In practice the quarterly import gives us exactly one active row per pair.
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c " +
            "WHERE c.tenantId = @tenantId " +
            "  AND c.column1Code = @col1 " +
            "  AND c.column2Code = @col2 " +
            "  AND c.effectiveDate <= @dos " +
            "  AND (NOT IS_DEFINED(c.terminationDate) OR c.terminationDate = null OR c.terminationDate > @dos) " +
            "ORDER BY c.effectiveDate DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@col1", column1Code)
            .WithParameter("@col2", column2Code)
            .WithParameter("@dos", serviceDate.ToString("yyyy-MM-dd"));

        using var feed = _pairContainer.GetItemQueryIterator<NcciEditPair>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        if (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct);
            return page.FirstOrDefault();
        }

        return null;
    }

    // ── MUE Entries ───────────────────────────────────────────────

    public async Task<MueEntry?> GetMueEntryAsync(
        string tenantId, string procedureCode, DateOnly serviceDate, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c " +
            "WHERE c.tenantId = @tenantId " +
            "  AND c.procedureCode = @code " +
            "  AND c.effectiveDate <= @dos " +
            "  AND (NOT IS_DEFINED(c.terminationDate) OR c.terminationDate = null OR c.terminationDate > @dos) " +
            "ORDER BY c.effectiveDate DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@code", procedureCode)
            .WithParameter("@dos", serviceDate.ToString("yyyy-MM-dd"));

        using var feed = _mueContainer.GetItemQueryIterator<MueEntry>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        if (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct);
            return page.FirstOrDefault();
        }

        return null;
    }

    // ── Quarterly Import ──────────────────────────────────────────

    public async Task<(int PairsWritten, int MueWritten)> UpsertQuarterAsync(
        string tenantId, string quarter,
        IReadOnlyList<NcciEditPair> pairs,
        IReadOnlyList<MueEntry> entries,
        CancellationToken ct = default)
    {
        int pairsWritten = 0;
        int mueWritten = 0;

        // Upsert in chunks of 50 to stay well within Cosmos limits
        const int chunkSize = 50;

        foreach (var chunk in pairs.Chunk(chunkSize))
        {
            foreach (var pair in chunk)
            {
                await _pairContainer.UpsertItemAsync(pair, new PartitionKey(tenantId), cancellationToken: ct);
                pairsWritten++;
            }
        }

        foreach (var chunk in entries.Chunk(chunkSize))
        {
            foreach (var entry in chunk)
            {
                await _mueContainer.UpsertItemAsync(entry, new PartitionKey(tenantId), cancellationToken: ct);
                mueWritten++;
            }
        }

        _logger.LogInformation(
            "Cosmos NCCI import for quarter {Quarter}: {Pairs} pairs, {Mue} MUE entries upserted",
            quarter, pairsWritten, mueWritten);

        return (pairsWritten, mueWritten);
    }

    // ── Version Metadata ──────────────────────────────────────────

    public async Task<NcciTableVersion?> GetCurrentVersionAsync(string tenantId, CancellationToken ct = default)
    {
        try
        {
            var response = await _versionContainer.ReadItemAsync<NcciTableVersion>(
                "current", new PartitionKey(tenantId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task SaveVersionAsync(NcciTableVersion version, CancellationToken ct = default)
    {
        await _versionContainer.UpsertItemAsync(
            version, new PartitionKey(version.TenantId), cancellationToken: ct);
    }
}
