using Azure.Storage.Blobs;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;

namespace EligibilityService.Services;

/// <summary>
/// Resolves BatchEligibility storage bindings from configuration.
///
/// Config section <c>BatchEligibility:StorageMode</c>:
///   <c>InMemory</c>   — in-process channel + in-memory job store (dev only).
///   <c>Persistent</c> — a document job store (MongoDB if configured, else
///                        Cosmos DB) + Blob payload store + IMessageBus queue.
///   <c>Auto</c>       — Persistent when a document store (Mongo or Cosmos)
///                        and Blob are configured; InMemory in Development;
///                        throws otherwise.
///
/// MongoDb is checked first — mirrors this service's own auto-detect pattern
/// (see Program.cs) and every other dual-backend service in this codebase
/// (e.g. accumulator-service): "Mongo if configured, else Cosmos". This is
/// what lets BatchEligibility run against self-hosted MongoDB, Cosmos DB for
/// MongoDB API, or Cosmos DB for NoSQL, not just the last of those.
///
/// The queue transport itself is owned by <see cref="IMessageBus"/>
/// (register via <c>AddChoMessaging</c>). Service Bus vs in-process channel
/// is decided there, not here.
/// </summary>
public static class BatchEligibilityServiceCollectionExtensions
{
    public static IServiceCollection AddBatchEligibilityStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var requestedMode = configuration["BatchEligibility:StorageMode"] ?? "Auto";
        var mongoCs = configuration["BatchEligibility:MongoDb:ConnectionString"];
        var cosmosCs = configuration["BatchEligibility:CosmosDb:ConnectionString"];
        var blobCs = configuration["BatchEligibility:BlobStorage:ConnectionString"];

        var hasPersistentConfig =
            (!string.IsNullOrWhiteSpace(mongoCs) || !string.IsNullOrWhiteSpace(cosmosCs)) &&
            !string.IsNullOrWhiteSpace(blobCs);

        var effectiveMode = ResolveMode(requestedMode, environment, hasPersistentConfig);

        if (effectiveMode == BatchStorageBackend.InMemory)
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "BatchEligibility:StorageMode resolved to InMemory outside Development. " +
                    "Configure BatchEligibility:MongoDb or BatchEligibility:CosmosDb, plus " +
                    "BatchEligibility:BlobStorage, or set StorageMode=Persistent explicitly.");
            }

            Console.WriteLine(
                "[dev] BatchEligibility storage = InMemory. Jobs do NOT survive restarts " +
                "and are not visible across replicas. Configure Mongo or Cosmos + Blob + a " +
                "Service Bus connection string under Messaging:ServiceBusConnectionString for " +
                "production.");

            services.AddSingleton<IBatchJobStore, InMemoryBatchJobStore>();
            services.AddSingleton<IBatchQueue, InMemoryBatchQueue>();
            services.AddSingleton<IBatchQueueProcessor, ChannelBatchQueueProcessor>();
            return services;
        }

        // Persistent
        RequireConfig(blobCs, "BatchEligibility:BlobStorage:ConnectionString");

        var blobContainerName = configuration["BatchEligibility:BlobStorage:Container"] ?? "batch-eligibility";
        var sbQueueName = configuration["BatchEligibility:ServiceBus:Queue"] ?? "batch-eligibility";
        var inlineMax = configuration.GetValue<int?>("BatchEligibility:InlineMaxBytes")
                        ?? BatchJobStore.DefaultInlineMaxBytes;
        var useMongo = !string.IsNullOrWhiteSpace(mongoCs);

        if (!useMongo)
        {
            RequireConfig(cosmosCs, "BatchEligibility:CosmosDb:ConnectionString");
        }

        services.AddSingleton<IBatchJobStore>(sp =>
        {
            var blobService = new BlobServiceClient(blobCs);
            var blobContainer = blobService.GetBlobContainerClient(blobContainerName);

            IBatchJobContainer jobContainer;
            if (useMongo)
            {
                var mongoDb = configuration["BatchEligibility:MongoDb:Database"] ?? "cho";
                var mongoCollection = configuration["BatchEligibility:MongoDb:Collection"] ?? "batch-jobs";
                var database = new MongoClient(mongoCs).GetDatabase(mongoDb);
                var collection = database.GetCollection<MongoContainerAdapter.JobDoc>(mongoCollection);
                var adapter = new MongoContainerAdapter(collection);
                adapter.EnsureIndexesAsync().GetAwaiter().GetResult();
                jobContainer = adapter;
            }
            else
            {
                var cosmosDb = configuration["BatchEligibility:CosmosDb:Database"] ?? "cho";
                var cosmosContainer = configuration["BatchEligibility:CosmosDb:Container"] ?? "batch-jobs";
                var cosmos = new CosmosClient(cosmosCs);
                jobContainer = new CosmosContainerAdapter(cosmos.GetContainer(cosmosDb, cosmosContainer));
            }

            return new BatchJobStore(
                jobContainer,
                new BlobContainerAdapter(blobContainer),
                inlineMax);
        });

        services.AddSingleton<IBatchQueue>(sp =>
            new ServiceBusBatchQueue(sp.GetRequiredService<IMessageBus>(), sbQueueName));
        services.AddSingleton<IBatchQueueProcessor>(sp =>
            new MessageBusBatchQueueProcessor(sp.GetRequiredService<IMessageBus>(), sbQueueName));

        return services;
    }

    private static BatchStorageBackend ResolveMode(
        string requestedMode, IHostEnvironment env, bool hasPersistentConfig)
    {
        return requestedMode.Trim().ToLowerInvariant() switch
        {
            "inmemory" => BatchStorageBackend.InMemory,
            "persistent" => BatchStorageBackend.Persistent,
            _ when hasPersistentConfig => BatchStorageBackend.Persistent,
            _ when env.IsDevelopment() => BatchStorageBackend.InMemory,
            _ => throw new InvalidOperationException(
                "BatchEligibility:StorageMode=Auto could not resolve: no Cosmos/Blob config and " +
                "not running in Development. Set StorageMode explicitly or provide the required " +
                "connection strings.")
        };
    }

    private static void RequireConfig(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"BatchEligibility storage mode is Persistent but '{key}' is not set.");
    }

    internal enum BatchStorageBackend { InMemory, Persistent }
}
