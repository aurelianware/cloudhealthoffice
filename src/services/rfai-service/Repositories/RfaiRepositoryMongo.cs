using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using RfaiService.Models;

namespace RfaiService.Repositories;

/// <summary>
/// MongoDB-backed implementation of <see cref="IRfaiRepository"/>.
/// Tenant isolation is enforced on every query via TenantId filter.
/// </summary>
public class RfaiRepositoryMongo : IRfaiRepository
{
    private readonly IMongoCollection<RfaiCase> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<RfaiRepositoryMongo> _logger;

    public RfaiRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<RfaiRepositoryMongo> logger)
    {
        _collection = database.GetCollection<RfaiCase>("RfaiCases");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        // Ensure indexes on startup (best effort)
        var indexKeys = Builders<RfaiCase>.IndexKeys;
        _collection.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<RfaiCase>(
                indexKeys.Ascending(r => r.TenantId).Ascending(r => r.AuthNumber)),
            new CreateIndexModel<RfaiCase>(
                indexKeys.Ascending(r => r.TenantId).Ascending(r => r.Status)),
            new CreateIndexModel<RfaiCase>(
                indexKeys.Ascending(r => r.TenantId).Ascending(r => r.CreatedAt))
        });
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            return "unknown";
        }
        return tenantId;
    }

    public async Task<RfaiCase> CreateAsync(RfaiCase rfaiCase)
    {
        rfaiCase.Id = Guid.NewGuid().ToString();
        rfaiCase.CreatedAt = DateTime.UtcNow;
        rfaiCase.UpdatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(rfaiCase);
        _logger.LogInformation("Created RFAI case {Id} for tenant {TenantId}", rfaiCase.Id, SanitizeForLog(rfaiCase.TenantId));
        return rfaiCase;
    }

    public async Task<RfaiCase?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<RfaiCase>.Filter.And(
            Builders<RfaiCase>.Filter.Eq(r => r.Id, id),
            Builders<RfaiCase>.Filter.Eq(r => r.TenantId, tenantId)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<RfaiCase>> GetByAuthNumberAsync(string tenantId, string authNumber)
    {
        var filter = Builders<RfaiCase>.Filter.And(
            Builders<RfaiCase>.Filter.Eq(r => r.TenantId, tenantId),
            Builders<RfaiCase>.Filter.Eq(r => r.AuthNumber, authNumber)
        );
        return await _collection.Find(filter)
            .SortByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    public async Task<RfaiCase> UpdateAsync(RfaiCase rfaiCase)
    {
        rfaiCase.UpdatedAt = DateTime.UtcNow;
        var filter = Builders<RfaiCase>.Filter.And(
            Builders<RfaiCase>.Filter.Eq(r => r.Id, rfaiCase.Id),
            Builders<RfaiCase>.Filter.Eq(r => r.TenantId, rfaiCase.TenantId)
        );
        await _collection.ReplaceOneAsync(filter, rfaiCase);
        _logger.LogInformation("Updated RFAI case {Id} status={Status}", rfaiCase.Id, rfaiCase.Status);
        return rfaiCase;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
