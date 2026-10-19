using System.Collections.Concurrent;
using AuthorizationService.Models;
using MongoDB.Driver;

namespace AuthorizationService.Repositories;

/// <summary>One asynchronous index initialization per client/collection, shared by request scopes.</summary>
public sealed class AuthorizationIndexes
{
    private readonly ConcurrentDictionary<(IMongoClient Client, string Collection), Lazy<Task>> _ready = new();

    public async Task EnsureAsync(IMongoCollection<Authorization> collection, CancellationToken ct = default)
    {
        var key = (collection.Database.Client, collection.CollectionNamespace.FullName);
        var work = _ready.GetOrAdd(key, _ => new Lazy<Task>(() => CreateAsync(collection),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            // A request cancelling must not cancel the initialization shared by other writers.
            await work.Value.WaitAsync(ct);
        }
        catch when (work.IsValueCreated && work.Value.IsFaulted)
        {
            ((ICollection<KeyValuePair<(IMongoClient, string), Lazy<Task>>>)_ready)
                .Remove(new KeyValuePair<(IMongoClient, string), Lazy<Task>>(key, work));
            throw;
        }
    }

    private static async Task CreateAsync(IMongoCollection<Authorization> collection)
    {
        var keys = Builders<Authorization>.IndexKeys;
        await collection.Indexes.CreateManyAsync([
            new CreateIndexModel<Authorization>(keys.Ascending(a => a.TenantId).Ascending(a => a.AuthorizationNumber),
                new CreateIndexOptions { Name = "authorization_tenant_number_unique", Unique = true }),
            new CreateIndexModel<Authorization>(keys.Ascending(a => a.TenantId).Ascending(a => a.Status),
                new CreateIndexOptions { Name = "authorization_tenant_status" }),
        ]);
    }
}

/// <summary>Fail startup on duplicate legacy numbers before the shared database serves requests.</summary>
public sealed class AuthorizationIndexStartup(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>() is AuthorizationRepositoryMongo mongo)
            await mongo.EnsureIndexesAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
