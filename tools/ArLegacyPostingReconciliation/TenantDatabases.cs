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

    /// <summary>
    /// The server(s) of the connection string, without credentials: <c>host:port</c>, sorted and
    /// joined by <c>;</c> (for <c>mongodb+srv</c>, the SRV host name). Recorded in the listing
    /// and the run record, and compared when a CSV is reconciled.
    /// </summary>
    public string Host => string.Join(";", _client.Settings.Servers
        .Select(s => $"{s.Host}:{s.Port}".ToLowerInvariant())
        .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>One line naming where the tool is about to read or write.</summary>
    public string Describe() =>
        $"Target: host {Host}, database {BaseDatabaseName}" +
        (UseTenantScoping ? $" (tenant scoping on: tenant data in {BaseDatabaseName}_<tenant>)" : " (tenant scoping off)");

    /// <summary>
    /// The operator names the database they mean to touch (<c>--confirm-database</c>); it must
    /// be the configured one, exactly.
    /// </summary>
    /// <exception cref="EnvironmentMismatchException">No name was given, or another one.</exception>
    public void Confirm(string? confirmDatabase)
    {
        if (string.IsNullOrWhiteSpace(confirmDatabase))
            throw new EnvironmentMismatchException(
                $"--confirm-database is required: the configured database is '{BaseDatabaseName}' on {Host}. Pass --confirm-database {BaseDatabaseName} if that is the environment you mean.");
        if (!string.Equals(confirmDatabase, BaseDatabaseName, StringComparison.Ordinal))
            throw new EnvironmentMismatchException(
                $"--confirm-database '{confirmDatabase}' is not the configured database '{BaseDatabaseName}' on {Host}. Nothing was read or changed; check MongoDb__ConnectionString and MongoDb__DatabaseName.");
    }

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

/// <summary>
/// The tool is pointed at an environment other than the one meant (database not confirmed,
/// CSV listed elsewhere), or that environment does not run a guard-capable ar-service.
/// Nothing is changed.
/// </summary>
public sealed class EnvironmentMismatchException(string message) : Exception(message);
