using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.Portal.Infrastructure;

/// <summary>
/// Creates MongoDB indexes for the TMPPM collections on startup.
/// Runs as a hosted background service so the UI path is never blocked on index creation.
/// </summary>
public class TmppmIndexService : BackgroundService
{
    private readonly IMongoClient _mongoClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TmppmIndexService> _logger;

    public TmppmIndexService(
        IMongoClient mongoClient,
        IConfiguration configuration,
        ILogger<TmppmIndexService> logger)
    {
        _mongoClient = mongoClient;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(
            TimeSpan.FromSeconds(
                _configuration.GetValue<int>("Mongo:Tmppm:IndexCreationDelaySeconds", 5)),
            stoppingToken);

        try
        {
            var databaseName = _configuration["Mongo:Tmppm:DatabaseName"] ?? "cho_terminology";
            var paRulesName = _configuration["Mongo:Tmppm:PaRulesCollectionName"] ?? "tmppm_pa_rules";
            var editionsName = _configuration["Mongo:Tmppm:EditionsCollectionName"] ?? "tmppm_editions";

            var db = _mongoClient.GetDatabase(databaseName);
            var paRules = db.GetCollection<BsonDocument>(paRulesName);
            var editions = db.GetCollection<BsonDocument>(editionsName);

            await paRules.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("procedureCodes"),
                new CreateIndexOptions { Name = "idx_procedure_codes" }), cancellationToken: stoppingToken);

            await paRules.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("category")
                    .Ascending("state"),
                new CreateIndexOptions { Name = "idx_category_state" }), cancellationToken: stoppingToken);

            await editions.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Descending("ingestedAt"),
                new CreateIndexOptions { Name = "idx_ingested_at" }), cancellationToken: stoppingToken);

            _logger.LogInformation("TMPPM indexes created or verified successfully");
        }
        catch (OperationCanceledException)
        {
            // Shutdown before indexes were created — safe to ignore
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create TMPPM indexes — they may already exist");
        }
    }
}
