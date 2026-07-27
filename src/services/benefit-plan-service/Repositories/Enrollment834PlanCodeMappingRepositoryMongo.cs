using BenefitPlanService.Models;
using MongoDB.Driver;

namespace BenefitPlanService.Repositories;

public class Enrollment834PlanCodeMappingRepositoryMongo : IEnrollment834PlanCodeMappingRepository
{
    private const string CollectionName = "Enrollment834PlanCodeMappings";
    private readonly IMongoCollection<Enrollment834PlanCodeMapping> _collection;

    public Enrollment834PlanCodeMappingRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<Enrollment834PlanCodeMapping>(CollectionName);

        var keys = Builders<Enrollment834PlanCodeMapping>.IndexKeys;
        _collection.Indexes.CreateMany(new[]
        {
            // Resolve() lookup shape — unique per tenant so two admins can't
            // silently shadow each other's mapping for the same code.
            new CreateIndexModel<Enrollment834PlanCodeMapping>(
                keys.Ascending(m => m.TenantId)
                    .Ascending(m => m.GroupNumber)
                    .Ascending(m => m.InsuranceLineCode)
                    .Ascending(m => m.ExternalPlanCode),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<Enrollment834PlanCodeMapping>(
                keys.Ascending(m => m.TenantId).Ascending(m => m.GroupNumber)),
        });
    }

    public async Task<Enrollment834PlanCodeMapping?> ResolveAsync(
        string tenantId, string groupNumber, string insuranceLineCode, string externalPlanCode,
        CancellationToken ct = default)
    {
        var b = Builders<Enrollment834PlanCodeMapping>.Filter;
        var filter = b.And(
            b.Eq(m => m.TenantId, tenantId),
            b.Eq(m => m.GroupNumber, groupNumber),
            b.Eq(m => m.InsuranceLineCode, insuranceLineCode),
            b.Eq(m => m.ExternalPlanCode, externalPlanCode));

        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<Enrollment834PlanCodeMapping> CreateAsync(
        Enrollment834PlanCodeMapping mapping, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(mapping.Id))
        {
            mapping.Id = Guid.NewGuid().ToString();
        }

        try
        {
            await _collection.InsertOneAsync(mapping, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicatePlanCodeMappingException(
                mapping.GroupNumber, mapping.InsuranceLineCode, mapping.ExternalPlanCode);
        }

        return mapping;
    }

    public async Task<IReadOnlyList<Enrollment834PlanCodeMapping>> ListAsync(
        string tenantId, string? groupNumber, CancellationToken ct = default)
    {
        var b = Builders<Enrollment834PlanCodeMapping>.Filter;
        var filter = string.IsNullOrEmpty(groupNumber)
            ? b.Eq(m => m.TenantId, tenantId)
            : b.And(b.Eq(m => m.TenantId, tenantId), b.Eq(m => m.GroupNumber, groupNumber));

        return await _collection.Find(filter).ToListAsync(ct);
    }

    public async Task<bool> DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var b = Builders<Enrollment834PlanCodeMapping>.Filter;
        var filter = b.And(b.Eq(m => m.TenantId, tenantId), b.Eq(m => m.Id, id));
        var result = await _collection.DeleteOneAsync(filter, ct);
        return result.DeletedCount > 0;
    }
}
