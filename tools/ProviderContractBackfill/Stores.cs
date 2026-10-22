using MongoDB.Bson;
using MongoDB.Driver;
using LegalModels = ProviderContractsService.Models;
using EngineModels = CloudHealthOffice.FeeScheduleEngine.Models;

namespace ProviderContractBackfill;

/// <summary>
/// Which collection each of the four stores ADR 016 describes lives in. All in
/// one database today; configurable because a deployment may have moved one.
/// </summary>
public sealed record Collections(
    string LegalContracts,
    string FfsRateConfigs,
    string CapitationRateConfigs,
    string EngineContracts,
    string FeeSchedules)
{
    public static Collections Default => new(
        "provider_contracts", "ffs_rate_configs", "capitation_rate_configs",
        "ProviderContracts", "FeeSchedules");
}

/// <summary>Raised when the database the tool is pointed at is not the one the operator named.</summary>
public sealed class EnvironmentMismatchException(string message) : Exception(message);

/// <summary>
/// The stores the backfill reads, and the one it writes.
///
/// <para>
/// The rate configs are read as <see cref="BsonDocument"/> rather than through
/// their services' models. Nothing has ever written the <c>FfsRateConfig</c>
/// collection — ffs-service has a model and a <c>Program.cs</c> and no
/// repository — so whatever is in it was put there by hand or by a seeder, and
/// a reader that insists on a shape would fail on the first surprise instead of
/// reporting it.
/// </para>
/// </summary>
public sealed class Stores
{
    private readonly IMongoDatabase _database;
    private readonly Collections _collections;

    public Stores(IMongoClient client, string databaseName, Collections collections)
    {
        DatabaseName = databaseName;
        _database = client.GetDatabase(databaseName);
        _collections = collections;
    }

    public string DatabaseName { get; }

    public string Describe()
        => $"database {DatabaseName}: legal={_collections.LegalContracts}, " +
           $"ffs={_collections.FfsRateConfigs}, capitation={_collections.CapitationRateConfigs}, " +
           $"engine={_collections.EngineContracts}, schedules={_collections.FeeSchedules}";

    /// <summary>
    /// Refuses unless the operator named this database on the command line. The
    /// connection string comes from the environment, so the only way to be sure
    /// which database is about to be written is to have the operator say it.
    /// </summary>
    public void Confirm(string? confirmDatabase)
    {
        if (string.IsNullOrWhiteSpace(confirmDatabase))
            throw new ArgumentException("--confirm-database is required; it must name the database above.");
        if (!string.Equals(confirmDatabase, DatabaseName, StringComparison.Ordinal))
        {
            throw new EnvironmentMismatchException(
                $"--confirm-database '{confirmDatabase}' is not the database this tool is connected to " +
                $"('{DatabaseName}'). Nothing was read or changed.");
        }
    }

    public async Task<List<string>> TenantsAsync()
    {
        var tenants = await _database
            .GetCollection<LegalModels.ProviderContract>(_collections.LegalContracts)
            .Distinct<string>("TenantId", FilterDefinition<LegalModels.ProviderContract>.Empty)
            .ToListAsync();

        var engineTenants = await _database
            .GetCollection<EngineModels.ProviderContract>(_collections.EngineContracts)
            .Distinct<string>("TenantId", FilterDefinition<EngineModels.ProviderContract>.Empty)
            .ToListAsync();

        return tenants.Concat(engineTenants)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
    }

    public Task<List<LegalModels.ProviderContract>> LegalContractsAsync(string tenantId)
        => _database.GetCollection<LegalModels.ProviderContract>(_collections.LegalContracts)
            .Find(Builders<LegalModels.ProviderContract>.Filter.Eq(c => c.TenantId, tenantId))
            .ToListAsync();

    public Task<List<EngineModels.ProviderContract>> EngineContractsAsync(string tenantId)
        => _database.GetCollection<EngineModels.ProviderContract>(_collections.EngineContracts)
            .Find(Builders<EngineModels.ProviderContract>.Filter.Eq(c => c.TenantId, tenantId))
            .ToListAsync();

    public async Task<HashSet<string>> FeeScheduleIdsAsync(string tenantId)
    {
        var ids = await _database.GetCollection<BsonDocument>(_collections.FeeSchedules)
            .Find(new BsonDocument("TenantId", tenantId))
            .Project(new BsonDocument { { "_id", 1 } })
            .ToListAsync();

        return ids
            .Select(d => d.GetValue("_id", BsonNull.Value))
            .Where(v => v.IsString)
            .Select(v => v.AsString)
            .ToHashSet(StringComparer.Ordinal);
    }

    public Task<List<BsonDocument>> FfsRateConfigsAsync(string tenantId)
        => RateConfigsAsync(_collections.FfsRateConfigs, tenantId);

    public Task<List<BsonDocument>> CapitationRateConfigsAsync(string tenantId)
        => RateConfigsAsync(_collections.CapitationRateConfigs, tenantId);

    private async Task<List<BsonDocument>> RateConfigsAsync(string collection, string tenantId)
    {
        // A collection nothing ever wrote may not exist; that is a clean result,
        // not a failure. Mongo returns an empty cursor for a missing collection.
        var names = await _database.ListCollectionNames().ToListAsync();
        if (!names.Contains(collection, StringComparer.Ordinal))
            return [];

        return await _database.GetCollection<BsonDocument>(collection)
            .Find(new BsonDocument("TenantId", tenantId))
            .ToListAsync();
    }

    /// <summary>
    /// Writes one canonical contract, by its deterministic id, so a second run
    /// replaces rather than duplicates.
    /// </summary>
    public async Task ReplaceEngineContractAsync(EngineModels.ProviderContract contract)
    {
        var collection = _database.GetCollection<EngineModels.ProviderContract>(_collections.EngineContracts);
        await collection.ReplaceOneAsync(
            Builders<EngineModels.ProviderContract>.Filter.Eq(c => c.Id, contract.Id),
            contract,
            new ReplaceOptions { IsUpsert = true });
    }
}
