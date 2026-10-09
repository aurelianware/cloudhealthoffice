using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.NcciEngine.Persistence;

/// <summary>
/// Cosmos DB implementation of INcciRepository.
///
/// Container layout (all partitioned by /tenantId; created at startup by
/// <see cref="NcciCosmosContainerInitializer"/> when missing):
///   NcciPairs      — NcciEditPair documents, id = stable key
///   MueEntries     — MueEntry documents, id = stable key
///   NcciVersion    — NcciTableVersion document, id = "current"
///   NcciLoadLedger — NcciLoadRecord documents, one per CMS file slot
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
        var names = NcciCosmosContainers.Resolve(configuration);
        _pairContainer    = cosmosClient.GetContainer(names.Database, names.Pairs);
        _mueContainer     = cosmosClient.GetContainer(names.Database, names.Mues);
        _versionContainer = cosmosClient.GetContainer(names.Database, names.Version);
        _ledgerContainer  = cosmosClient.GetContainer(names.Database, names.LoadLedger);
        _logger = logger;
    }

    private readonly Container _ledgerContainer;

    // Setting filter: null @setting matches every row; otherwise rows of that
    // setting, plus setting-less (seed / legacy) rows when @unscoped is true.
    private const string SettingClause =
        "  AND (@setting = null OR c.setting = @setting " +
        "       OR (@unscoped = true AND (NOT IS_DEFINED(c.setting) OR c.setting = null))) ";

    // ── NCCI Edit Pairs ────────────────────────────────────────────

    public async Task<NcciEditPair?> GetEditPairAsync(
        string tenantId, string column1Code, string column2Code,
        DateOnly serviceDate, string? setting = null, bool includeUnscoped = true, CancellationToken ct = default)
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
            SettingClause +
            "ORDER BY c.effectiveDate DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@col1", column1Code)
            .WithParameter("@col2", column2Code)
            .WithParameter("@dos", serviceDate.ToString("yyyy-MM-dd"))
            .WithParameter("@setting", setting)
            .WithParameter("@unscoped", includeUnscoped);

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
        string tenantId, string procedureCode, DateOnly serviceDate,
        string? setting = null, bool includeUnscoped = true, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c " +
            "WHERE c.tenantId = @tenantId " +
            "  AND c.procedureCode = @code " +
            "  AND c.effectiveDate <= @dos " +
            "  AND (NOT IS_DEFINED(c.terminationDate) OR c.terminationDate = null OR c.terminationDate > @dos) " +
            SettingClause +
            "ORDER BY c.effectiveDate DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@code", procedureCode)
            .WithParameter("@dos", serviceDate.ToString("yyyy-MM-dd"))
            .WithParameter("@setting", setting)
            .WithParameter("@unscoped", includeUnscoped);

        using var feed = _mueContainer.GetItemQueryIterator<MueEntry>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        if (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct);
            return page.FirstOrDefault();
        }

        return null;
    }

    // ── CMS snapshot reconciliation ───────────────────────────────
    // Dates are stored as ISO-8601 strings; "yyyy-MM-dd" prefixes compare
    // lexicographically, as in the lookup queries above.

    public async Task<(int Expired, int Deleted)> ReconcileMueSnapshotAsync(
        string tenantId, string setting, DateTime quarterStart,
        IReadOnlySet<string> retainedCodes, CancellationToken ct = default)
    {
        var start = quarterStart.ToString("yyyy-MM-dd");
        var query = new QueryDefinition(
            "SELECT * FROM c " +
            "WHERE c.tenantId = @tenantId " +
            "  AND c.setting = @setting " +
            "  AND (STARTSWITH(c.effectiveDate, @start) OR (c.effectiveDate < @start " +
            "       AND (NOT IS_DEFINED(c.terminationDate) OR c.terminationDate = null OR c.terminationDate > @start)))")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@setting", setting)
            .WithParameter("@start", start);

        int expired = 0, deleted = 0;
        var pk = new PartitionKey(tenantId);
        using var feed = _mueContainer.GetItemQueryIterator<MueEntry>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = pk });

        while (feed.HasMoreResults)
        {
            foreach (var entry in await feed.ReadNextAsync(ct))
            {
                if (retainedCodes.Contains(entry.ProcedureCode)) continue;
                if (entry.EffectiveDate.Date == quarterStart.Date)
                {
                    await _mueContainer.DeleteItemAsync<MueEntry>(entry.Id, pk, cancellationToken: ct);
                    deleted++;
                }
                else
                {
                    entry.TerminationDate = quarterStart;
                    await _mueContainer.UpsertItemAsync(entry, pk, cancellationToken: ct);
                    expired++;
                }
            }
        }

        return (expired, deleted);
    }

    public async Task<(int Expired, int Deleted)> ReconcilePtpSnapshotAsync(
        string tenantId, string sourceKey, string quarter, DateTime quarterStart,
        string loadId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.sourceKey = @sourceKey AND c.loadId != @loadId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@sourceKey", sourceKey)
            .WithParameter("@loadId", loadId);

        int expired = 0, deleted = 0;
        var pk = new PartitionKey(tenantId);
        using var feed = _pairContainer.GetItemQueryIterator<NcciEditPair>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = pk });

        while (feed.HasMoreResults)
        {
            foreach (var pair in await feed.ReadNextAsync(ct))
            {
                if (pair.SourceQuarter == quarter)
                {
                    await _pairContainer.DeleteItemAsync<NcciEditPair>(pair.Id, pk, cancellationToken: ct);
                    deleted++;
                }
                else if (pair.TerminationDate is null || pair.TerminationDate > quarterStart)
                {
                    pair.TerminationDate = quarterStart;
                    await _pairContainer.UpsertItemAsync(pair, pk, cancellationToken: ct);
                    expired++;
                }
            }
        }

        return (expired, deleted);
    }

    // ── CMS Load Ledger ───────────────────────────────────────────

    public async Task<NcciLoadRecord?> GetLoadRecordAsync(string tenantId, string id, CancellationToken ct = default)
    {
        try
        {
            var response = await _ledgerContainer.ReadItemAsync<NcciLoadRecord>(
                id, new PartitionKey(tenantId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task SaveLoadRecordAsync(NcciLoadRecord record, CancellationToken ct = default)
    {
        await _ledgerContainer.UpsertItemAsync(
            record, new PartitionKey(record.TenantId), cancellationToken: ct);
    }

    public async Task<IReadOnlyList<NcciLoadRecord>> ListLoadRecordsAsync(
        string tenantId, string? quarter, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND (@quarter = null OR c.quarter = @quarter)")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@quarter", quarter);

        var records = new List<NcciLoadRecord>();
        using var feed = _ledgerContainer.GetItemQueryIterator<NcciLoadRecord>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (feed.HasMoreResults)
            records.AddRange(await feed.ReadNextAsync(ct));
        return records;
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
