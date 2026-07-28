using CloudHealthOffice.NcciEngine.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CloudHealthOffice.NcciEngine.Persistence;

/// <summary>
/// Ensures NCCI lookup indexes once during host startup instead of from every
/// scoped repository construction on the claim-processing hot path.
/// </summary>
internal sealed class NcciMongoIndexInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NcciMongoIndexInitializer> _logger;

    public NcciMongoIndexInitializer(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<NcciMongoIndexInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    internal static CreateIndexModel<NcciEditPair> BuildPairIndex() =>
        new(
            Builders<NcciEditPair>.IndexKeys
                .Ascending(pair => pair.TenantId)
                .Ascending(pair => pair.Column1Code)
                .Ascending(pair => pair.Column2Code)
                .Ascending(pair => pair.EffectiveDate),
            new CreateIndexOptions { Name = "ix_ncci_pair_lookup" });

    internal static CreateIndexModel<MueEntry> BuildMueIndex() =>
        new(
            Builders<MueEntry>.IndexKeys
                .Ascending(entry => entry.TenantId)
                .Ascending(entry => entry.ProcedureCode)
                .Ascending(entry => entry.EffectiveDate),
            new CreateIndexOptions { Name = "ix_ncci_mue_lookup" });

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetService<IMongoDatabase>();
        if (database is null)
        {
            _logger.LogDebug("Skipping NCCI Mongo index setup because IMongoDatabase is not registered.");
            return;
        }

        var pairCollectionName = _configuration["NcciEngine:MongoPairCollection"] ?? "ncci_pairs";
        var mueCollectionName = _configuration["NcciEngine:MongoMueCollection"] ?? "mue_entries";

        await database.GetCollection<NcciEditPair>(pairCollectionName)
            .Indexes.CreateOneAsync(BuildPairIndex(), cancellationToken: cancellationToken);
        await database.GetCollection<MueEntry>(mueCollectionName)
            .Indexes.CreateOneAsync(BuildMueIndex(), cancellationToken: cancellationToken);

        _logger.LogInformation(
            "NCCI Mongo lookup indexes ensured on database '{Database}'.",
            database.DatabaseNamespace.DatabaseName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
