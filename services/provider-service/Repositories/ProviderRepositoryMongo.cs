using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using ProviderService.Models;

namespace ProviderService.Repositories;

public class ProviderRepositoryMongo : IProviderRepository
{
    private readonly IMongoCollection<Provider> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ProviderRepositoryMongo> _logger;

    public ProviderRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ProviderRepositoryMongo> logger)
    {
        _collection = database.GetCollection<Provider>("Providers");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        CreateIndexes();
    }

    private void CreateIndexes()
    {
        var keys = Builders<Provider>.IndexKeys;
        var models = new List<CreateIndexModel<Provider>>
        {
            new CreateIndexModel<Provider>(keys.Ascending(p => p.TenantId).Ascending(p => p.NPI)),
            new CreateIndexModel<Provider>(keys.Ascending(p => p.TenantId).Ascending(p => p.LastName)),
            new CreateIndexModel<Provider>(keys.Ascending(p => p.TenantId).Ascending(p => p.OrganizationName)),
            new CreateIndexModel<Provider>(keys.Ascending(p => p.TenantId).Ascending(p => p.ZipCode)),
            // Multikey index for network participations?
            new CreateIndexModel<Provider>(keys.Ascending(p => p.TenantId).Ascending("NetworkParticipations.PlanId"))
        };
        _collection.Indexes.CreateMany(models);
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
           // For migration safety, we might return a default or just let it fail at runtime if strictly required.
           // throw new InvalidOperationException("TenantId not found in request context");
           return string.Empty; 
        }
        return tenantId;
    }

    public async Task<Provider?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Provider>.Filter.And(
            Builders<Provider>.Filter.Eq(p => p.Id, id),
            Builders<Provider>.Filter.Eq(p => p.TenantId, tenantId)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<Provider?> GetByNPIAsync(string npi)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Provider>.Filter.And(
            Builders<Provider>.Filter.Eq(p => p.NPI, npi),
            Builders<Provider>.Filter.Eq(p => p.TenantId, tenantId)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<Provider>> SearchAsync(
        string? name,
        string? specialty,
        string? zipCode,
        string? state,
        string? planId,
        LineOfBusiness? lineOfBusiness,
        ProviderType? providerType,
        bool? acceptingNewPatients,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();
        var builder = Builders<Provider>.Filter;
        
        // Base filters
        var filter = builder.And(
            builder.Eq(p => p.TenantId, tenantId),
            builder.Eq(p => p.Status, ProviderStatus.Active) // Enum mapping assumes exact match
        );

        if (!string.IsNullOrEmpty(name))
        {
            // Case-insensitive regex for Name (First, Last, or Org)
            var regex = new BsonRegularExpression(name, "i");
            var nameFilter = builder.Or(
                builder.Regex(p => p.FirstName, regex),
                builder.Regex(p => p.LastName, regex),
                builder.Regex(p => p.OrganizationName, regex)
            );
            filter = builder.And(filter, nameFilter);
        }

        if (!string.IsNullOrEmpty(specialty))
        {
            filter = builder.And(filter, builder.Regex(p => p.PrimarySpecialty, new BsonRegularExpression(specialty, "i")));
        }

        if (!string.IsNullOrEmpty(zipCode))
        {
            filter = builder.And(filter, builder.Eq(p => p.ZipCode, zipCode));
        }

        if (!string.IsNullOrEmpty(state))
        {
            filter = builder.And(filter, builder.Eq(p => p.State, state));
        }

        if (providerType.HasValue)
        {
            filter = builder.And(filter, builder.Eq(p => p.ProviderType, providerType.Value));
        }

        if (acceptingNewPatients.HasValue)
        {
             filter = builder.And(filter, builder.Eq(p => p.AcceptingNewPatients, acceptingNewPatients.Value));
        }

        // Network Participation (Array ElemMatch)
        if (!string.IsNullOrEmpty(planId) || lineOfBusiness.HasValue)
        {
            var netBuilder = Builders<NetworkParticipation>.Filter;
            FilterDefinition<NetworkParticipation> netFilter = FilterDefinition<NetworkParticipation>.Empty;

            if (!string.IsNullOrEmpty(planId))
                netFilter = netBuilder.Eq(n => n.PlanId, planId);

            if (lineOfBusiness.HasValue)
            {
                var lobFilter = netBuilder.Eq(n => n.LineOfBusiness, lineOfBusiness.Value);
                netFilter = netFilter == FilterDefinition<NetworkParticipation>.Empty 
                    ? lobFilter 
                    : netBuilder.And(netFilter, lobFilter);
            }

            filter = builder.And(filter, builder.ElemMatch(p => p.NetworkParticipations, netFilter));
        }

        // Sort by LastName then OrgName
        var sort = Builders<Provider>.Sort.Ascending(p => p.LastName).Ascending(p => p.OrganizationName);

        return await _collection.Find(filter)
            .Sort(sort)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<Provider> CreateAsync(Provider provider)
    {
        var tenantId = GetTenantId();
        if (string.IsNullOrEmpty(provider.TenantId))
        {
            provider.TenantId = tenantId;
        }

        if (string.IsNullOrEmpty(provider.Id))
        {
            provider.Id = Guid.NewGuid().ToString();
        }

        await _collection.InsertOneAsync(provider);
        return provider;
    }

    public async Task<Provider> UpdateAsync(Provider provider)
    {
        var tenantId = GetTenantId();
        if (provider.TenantId != tenantId)
        {
            throw new InvalidOperationException("Cross-tenant updates not allowed");
        }

        var filter = Builders<Provider>.Filter.And(
            Builders<Provider>.Filter.Eq(p => p.Id, provider.Id),
            Builders<Provider>.Filter.Eq(p => p.TenantId, tenantId)
        );

        var result = await _collection.ReplaceOneAsync(filter, provider);
        if (result.MatchedCount == 0)
        {
             throw new Exception($"Provider {provider.Id} not found");
        }

        return provider;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Provider>.Filter.And(
            Builders<Provider>.Filter.Eq(p => p.Id, id),
            Builders<Provider>.Filter.Eq(p => p.TenantId, tenantId)
        );
        await _collection.DeleteOneAsync(filter);
    }
}
