using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using System;

namespace CloudHealthOffice.Infrastructure.DocumentStore;

/// <summary>
/// Extension methods for configuring cloud-agnostic document store.
/// </summary>
public static class DocumentStoreExtensions
{
    /// <summary>
    /// Add document store based on CloudProvider configuration.
    /// Supports "Azure" (Cosmos DB) or "DigitalOcean" (MongoDB).
    /// </summary>
    public static IServiceCollection AddDocumentStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var cloudProvider = configuration["CloudProvider"] ?? "Azure";
        
        if (cloudProvider.Equals("Azure", StringComparison.OrdinalIgnoreCase))
        {
            AddAzureCosmosDb(services, configuration);
        }
        else if (cloudProvider.Equals("DigitalOcean", StringComparison.OrdinalIgnoreCase))
        {
            AddMongoDb(services, configuration);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported CloudProvider: {cloudProvider}. Valid values are 'Azure' or 'DigitalOcean'.");
        }

        return services;
    }

    private static void AddAzureCosmosDb(IServiceCollection services, IConfiguration configuration)
    {
        var endpoint = configuration["CosmosDb:Endpoint"] 
            ?? throw new InvalidOperationException("CosmosDb:Endpoint is required for Azure cloud provider");
        var key = configuration["CosmosDb:Key"] 
            ?? throw new InvalidOperationException("CosmosDb:Key is required for Azure cloud provider");

        services.AddSingleton<CosmosClient>(sp =>
        {
            return new CosmosClient(endpoint, key, new CosmosClientOptions
            {
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                },
                ConnectionMode = Microsoft.Azure.Cosmos.ConnectionMode.Direct,
                MaxRetryAttemptsOnRateLimitedRequests = 9,
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30)
            });
        });

        // Register IDocumentStore factory
        services.AddScoped(typeof(IDocumentStore<>), typeof(CosmosDocumentStore<>));
    }

    private static void AddMongoDb(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["MongoDB:ConnectionString"] 
            ?? throw new InvalidOperationException("MongoDB:ConnectionString is required for DigitalOcean cloud provider");

        services.AddSingleton<IMongoClient>(sp =>
        {
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ServerApi = new ServerApi(ServerApiVersion.V1);
            return new MongoClient(settings);
        });

        // Register IDocumentStore factory
        services.AddScoped(typeof(IDocumentStore<>), typeof(MongoDocumentStore<>));
    }

    /// <summary>
    /// Create a specific document store instance.
    /// Used by repositories to initialize their container/collection.
    /// </summary>
    public static IDocumentStore<T> CreateDocumentStore<T>(
        this IServiceProvider serviceProvider,
        string databaseName,
        string containerName,
        string partitionKeyPath = "/tenantId") where T : class
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var cloudProvider = configuration["CloudProvider"] ?? "Azure";

        if (cloudProvider.Equals("Azure", StringComparison.OrdinalIgnoreCase))
        {
            var cosmosClient = serviceProvider.GetRequiredService<CosmosClient>();
            return new CosmosDocumentStore<T>(cosmosClient, databaseName, containerName, partitionKeyPath);
        }
        else if (cloudProvider.Equals("DigitalOcean", StringComparison.OrdinalIgnoreCase))
        {
            var mongoClient = serviceProvider.GetRequiredService<IMongoClient>();
            var partitionKeyField = partitionKeyPath.TrimStart('/'); // Remove leading slash for MongoDB
            return new MongoDocumentStore<T>(mongoClient, databaseName, containerName, partitionKeyField);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported CloudProvider: {cloudProvider}. Valid values are 'Azure' or 'DigitalOcean'.");
        }
    }
}
