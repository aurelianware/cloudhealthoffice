using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;

namespace EligibilityService.Services;

/// <summary>
/// Resolves BatchEligibility storage bindings from configuration.
///
/// Config section <c>BatchEligibility:StorageMode</c>:
///   InMemory   — in-process channel + in-memory job store (dev only).
///   Persistent — Cosmos job store + Blob payload store + Service Bus queue.
///   Auto       — Persistent when both Cosmos and Service Bus are configured;
///                 InMemory + warn in dev when they're not; throws otherwise.
///
/// Never registers both in-memory and persistent implementations.
/// </summary>
public static class BatchEligibilityServiceCollectionExtensions
{
    public static IServiceCollection AddBatchEligibilityStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var requestedMode = configuration["BatchEligibility:StorageMode"] ?? "Auto";
        var cosmosCs = configuration["BatchEligibility:CosmosDb:ConnectionString"];
        var blobCs = configuration["BatchEligibility:BlobStorage:ConnectionString"];
        var serviceBusCs = configuration["BatchEligibility:ServiceBus:ConnectionString"];

        var hasPersistentConfig =
            !string.IsNullOrWhiteSpace(cosmosCs) &&
            !string.IsNullOrWhiteSpace(blobCs) &&
            !string.IsNullOrWhiteSpace(serviceBusCs);

        var effectiveMode = ResolveMode(requestedMode, environment, hasPersistentConfig);

        if (effectiveMode == BatchStorageBackend.InMemory)
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "BatchEligibility:StorageMode resolved to InMemory outside Development. " +
                    "Configure BatchEligibility:CosmosDb, BatchEligibility:BlobStorage and " +
                    "BatchEligibility:ServiceBus, or set StorageMode=Persistent explicitly.");
            }

            Console.WriteLine(
                "[dev] BatchEligibility storage = InMemory. Jobs do NOT survive restarts " +
                "and are not visible across replicas. Configure Cosmos + Blob + Service Bus " +
                "for production.");

            services.AddSingleton<IBatchJobStore, InMemoryBatchJobStore>();
            services.AddSingleton<IBatchQueue, InMemoryBatchQueue>();
            services.AddSingleton<IBatchQueueProcessor, ChannelBatchQueueProcessor>();
            return services;
        }

        // Persistent
        RequireConfig(cosmosCs, "BatchEligibility:CosmosDb:ConnectionString");
        RequireConfig(blobCs, "BatchEligibility:BlobStorage:ConnectionString");
        RequireConfig(serviceBusCs, "BatchEligibility:ServiceBus:ConnectionString");

        var cosmosDb = configuration["BatchEligibility:CosmosDb:Database"] ?? "cho";
        var cosmosContainer = configuration["BatchEligibility:CosmosDb:Container"] ?? "batch-jobs";
        var blobContainerName = configuration["BatchEligibility:BlobStorage:Container"] ?? "batch-eligibility";
        var sbQueueName = configuration["BatchEligibility:ServiceBus:Queue"] ?? "batch-eligibility";
        var inlineMax = configuration.GetValue<int?>("BatchEligibility:InlineMaxBytes")
                        ?? CosmosBatchJobStore.DefaultInlineMaxBytes;

        services.AddSingleton<IBatchJobStore>(sp =>
        {
            var cosmos = new CosmosClient(cosmosCs);
            var container = cosmos.GetContainer(cosmosDb, cosmosContainer);

            var blobService = new BlobServiceClient(blobCs);
            var blobContainer = blobService.GetBlobContainerClient(blobContainerName);

            return new CosmosBatchJobStore(
                new CosmosContainerAdapter(container),
                new BlobContainerAdapter(blobContainer),
                inlineMax);
        });

        services.AddSingleton<ServiceBusClient>(_ => new ServiceBusClient(serviceBusCs));
        services.AddSingleton<IBatchQueueSender>(sp =>
            new ServiceBusSenderAdapter(sp.GetRequiredService<ServiceBusClient>(), sbQueueName));
        services.AddSingleton<IBatchQueue, ServiceBusBatchQueue>();
        services.AddSingleton<IBatchQueueProcessor>(sp =>
            new ServiceBusBatchQueueProcessor(sp.GetRequiredService<ServiceBusClient>(), sbQueueName));

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
                "BatchEligibility:StorageMode=Auto could not resolve: no Cosmos/Blob/Service Bus " +
                "config and not running in Development. Set StorageMode explicitly or provide " +
                "the required connection strings.")
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
