using CloudHealthOffice.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace ArLegacyPostingReconciliation;

/// <summary>
/// Finds the database ar-service uses for a tenant, the same way the service does
/// (<see cref="MongoDbConnectionFactory"/>): the base database, or
/// <c>{base}_{tenant}</c> when <c>MongoDb:UseTenantScoping</c> is on.
/// </summary>
public sealed class TenantDatabases
{
    private readonly IMongoClient _client;
    private readonly MongoDbConnectionFactory _factory;

    public TenantDatabases(IMongoClient client, string baseDatabaseName, bool useTenantScoping)
    {
        _client = client;
        BaseDatabaseName = baseDatabaseName;
        UseTenantScoping = useTenantScoping;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MongoDb:DatabaseName"] = baseDatabaseName,
            ["MongoDb:UseTenantScoping"] = useTenantScoping ? "true" : "false"
        }).Build();
        _factory = new MongoDbConnectionFactory(client, new HttpContextAccessor(), config);
    }

    public string BaseDatabaseName { get; }
    public bool UseTenantScoping { get; }

    /// <summary>The database that holds the run records (the base database).</summary>
    public IMongoDatabase Base => _client.GetDatabase(BaseDatabaseName);

    public IMongoDatabase ForTenant(string tenantId) => _factory.GetDatabase(tenantId);

    /// <summary>Every database that may hold ar-service data.</summary>
    public async Task<List<IMongoDatabase>> AllAsync()
    {
        if (!UseTenantScoping)
            return [Base];
        var names = await (await _client.ListDatabaseNamesAsync()).ToListAsync();
        return names
            .Where(n => n == BaseDatabaseName || n.StartsWith(BaseDatabaseName + "_", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => _client.GetDatabase(n))
            .ToList();
    }

    /// <summary>A request context carrying the tenant, for the ar-service repositories.</summary>
    public static IHttpContextAccessor AccessorFor(string tenantId)
    {
        var context = new DefaultHttpContext();
        context.Items["TenantId"] = tenantId;
        return new HttpContextAccessor { HttpContext = context };
    }
}
