using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Data;

/// <summary>
/// Tenant-aware MongoDB connection factory. Resolves database names scoped by tenant ID
/// using the pattern: {BaseDatabaseName}_{TenantId}.
/// </summary>
public class MongoDbConnectionFactory
{
    private readonly IMongoClient _client;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string _baseDatabaseName;
    private readonly bool _useTenantScoping;

    public MongoDbConnectionFactory(
        IMongoClient client,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration)
    {
        _client = client;
        _httpContextAccessor = httpContextAccessor;
        _baseDatabaseName = configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        _useTenantScoping = configuration.GetValue<bool>("MongoDb:UseTenantScoping", false);
    }

    /// <summary>
    /// Returns the IMongoDatabase for the current tenant.
    /// If tenant scoping is enabled, the database name is "{BaseDatabaseName}_{TenantId}".
    /// Otherwise, returns the base database (tenant isolation via partition keys).
    /// </summary>
    public IMongoDatabase GetDatabase()
    {
        var dbName = _useTenantScoping ? GetTenantScopedDatabaseName() : _baseDatabaseName;
        return _client.GetDatabase(dbName);
    }

    /// <summary>
    /// Returns the IMongoDatabase for a specific tenant by ID.
    /// </summary>
    public IMongoDatabase GetDatabase(string tenantId)
    {
        var dbName = _useTenantScoping
            ? $"{_baseDatabaseName}_{SanitizeTenantId(tenantId)}"
            : _baseDatabaseName;
        return _client.GetDatabase(dbName);
    }

    private string GetTenantScopedDatabaseName()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();

        if (string.IsNullOrEmpty(tenantId))
            return _baseDatabaseName;

        return $"{_baseDatabaseName}_{SanitizeTenantId(tenantId)}";
    }

    private static string SanitizeTenantId(string tenantId)
    {
        // MongoDB database names cannot contain: /\. "$*<>:|?
        // Replace invalid characters with underscore
        var sanitized = tenantId;
        foreach (var c in new[] { '/', '\\', '.', ' ', '"', '$', '*', '<', '>', ':', '|', '?' })
        {
            sanitized = sanitized.Replace(c, '_');
        }
        return sanitized;
    }
}
