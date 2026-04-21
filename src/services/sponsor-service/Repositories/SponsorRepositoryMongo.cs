using SponsorService.Models;
using MongoDB.Driver;

namespace SponsorService.Repositories;

public class SponsorRepositoryMongo : ISponsorRepository
{
    private readonly IMongoCollection<Sponsor> _collection;
    private readonly ILogger<SponsorRepositoryMongo> _logger;

    public SponsorRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<SponsorRepositoryMongo> logger)
    {
        var collectionName = configuration["CosmosDb:ContainerName"] ?? "Sponsors";
        _collection = database.GetCollection<Sponsor>(collectionName);
        _logger = logger;
    }

    public async Task<Sponsor?> GetByIdAsync(string tenantId, string id)
    {
        var filter = Builders<Sponsor>.Filter.And(
            Builders<Sponsor>.Filter.Eq(x => x.Id, id),
            Builders<Sponsor>.Filter.Eq(x => x.TenantId, tenantId)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<Sponsor?> GetByGroupNumberAsync(string tenantId, string groupNumber)
    {
        var filter = Builders<Sponsor>.Filter.And(
            Builders<Sponsor>.Filter.Eq(x => x.GroupNumber, groupNumber),
            Builders<Sponsor>.Filter.Eq(x => x.TenantId, tenantId)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<(IEnumerable<Sponsor> Items, string? ContinuationToken, int TotalCount)> GetPagedAsync(
        string tenantId,
        SponsorStatus? status = null,
        bool activeOnly = false,
        LineOfBusiness? lineOfBusiness = null,
        int pageSize = 20,
        string? continuationToken = null)
    {
        var builder = Builders<Sponsor>.Filter;
        var filter = builder.Eq(x => x.TenantId, tenantId);

        if (activeOnly)
        {
            filter &= builder.Eq(x => x.Status, SponsorStatus.Active);
        }
        else if (status.HasValue)
        {
            filter &= builder.Eq(x => x.Status, status.Value);
        }

        if (lineOfBusiness.HasValue)
        {
            filter &= builder.Eq(x => x.LineOfBusiness, lineOfBusiness.Value);
        }

        int skip = 0;
        if (!string.IsNullOrEmpty(continuationToken) && int.TryParse(continuationToken, out int tokenSkip))
        {
            skip = tokenSkip;
        }

        var totalCount = await _collection.CountDocumentsAsync(filter);
        var items = await _collection.Find(filter)
            .SortByDescending(x => x.LastUpdatedDate)
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync();

        string? nextContinuationToken = null;
        if (skip + items.Count < totalCount)
        {
            nextContinuationToken = (skip + items.Count).ToString();
        }

        return (items, nextContinuationToken, (int)totalCount);
    }

    public async Task<Sponsor> CreateAsync(Sponsor sponsor)
    {
        sponsor.Id ??= Guid.NewGuid().ToString();
        sponsor.CreatedDate = DateTime.UtcNow;
        sponsor.LastUpdatedDate = DateTime.UtcNow;
        
        await _collection.InsertOneAsync(sponsor);
        return sponsor;
    }

    public async Task<Sponsor> UpdateAsync(Sponsor sponsor)
    {
        sponsor.LastUpdatedDate = DateTime.UtcNow;

        var filter = Builders<Sponsor>.Filter.And(
            Builders<Sponsor>.Filter.Eq(x => x.Id, sponsor.Id),
            Builders<Sponsor>.Filter.Eq(x => x.TenantId, sponsor.TenantId)
        );
        
        await _collection.ReplaceOneAsync(filter, sponsor);
        return sponsor;
    }

    public async Task DeleteAsync(string tenantId, string id)
    {
        var filter = Builders<Sponsor>.Filter.And(
            Builders<Sponsor>.Filter.Eq(x => x.Id, id),
            Builders<Sponsor>.Filter.Eq(x => x.TenantId, tenantId)
        );
        await _collection.DeleteOneAsync(filter);
    }

    public async Task<bool> ExistsAsync(string tenantId, string groupNumber)
    {
        var filter = Builders<Sponsor>.Filter.And(
            Builders<Sponsor>.Filter.Eq(x => x.GroupNumber, groupNumber),
            Builders<Sponsor>.Filter.Eq(x => x.TenantId, tenantId)
        );
        return await _collection.Find(filter).AnyAsync();
    }

    public async Task<int> GetCountAsync(string tenantId, SponsorStatus? status = null)
    {
        var builder = Builders<Sponsor>.Filter;
        var filter = builder.Eq(x => x.TenantId, tenantId);

        if (status.HasValue)
        {
            filter &= builder.Eq(x => x.Status, status.Value);
        }

        return (int)await _collection.CountDocumentsAsync(filter);
    }
}
