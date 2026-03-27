using CoverageService.Models;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CoverageService.Repositories;

/// <summary>
/// MongoDB repository for Coverage entities.
/// Implements the same interface as the Cosmos DB repository for portability.
/// </summary>
public class CoverageRepositoryMongo : ICoverageRepository
{
    private readonly IMongoCollection<Coverage> _collection;
    private const string CollectionName = "Coverage";

    public CoverageRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<Coverage>(CollectionName);
        
        // Ensure indexes exist (best effort on startup)
        var indexKeys = Builders<Coverage>.IndexKeys;
        var indexModels = new List<CreateIndexModel<Coverage>>
        {
            new CreateIndexModel<Coverage>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.MemberId)),
            new CreateIndexModel<Coverage>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.GroupNumber)),
            // Compound index for active coverage search
            new CreateIndexModel<Coverage>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.MemberId).Ascending(c => c.Status).Ascending(c => c.EffectiveDate)),
            // PCP panel roster lookup (capitation-service queries by provider NPI)
            new CreateIndexModel<Coverage>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.PcpNpi).Ascending(c => c.Status))
        };
        
        _collection.Indexes.CreateMany(indexModels);
    }

    public async Task<Coverage?> GetByIdAsync(string tenantId, string id)
    {
        var filter = Builders<Coverage>.Filter.And(
            Builders<Coverage>.Filter.Eq(c => c.TenantId, tenantId),
            Builders<Coverage>.Filter.Eq(c => c.Id, id)
        );

        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<List<Coverage>> GetActiveCoverageByMemberIdAsync(
        string tenantId,
        string memberId,
        DateTime serviceDate,
        string? insuranceLineCode = null)
    {
        // Cosmos Query:
        // WHERE c.tenantId = @tenantId 
        // AND c.memberId = @memberId
        // AND c.status = @activeStatus
        // AND c.effectiveDate <= @serviceDate
        // AND (NOT IS_DEFINED(c.terminationDate) OR c.terminationDate >= @serviceDate)

        var builder = Builders<Coverage>.Filter;
        var date = serviceDate.Date;

        var filter = builder.And(
            builder.Eq(c => c.TenantId, tenantId),
            builder.Eq(c => c.MemberId, memberId),
            builder.Eq(c => c.Status, CoverageStatus.Active),
            builder.Lte(c => c.EffectiveDate, date),
            builder.Or(
                builder.Eq(c => c.TerminationDate, null),
                builder.Gte(c => c.TerminationDate, date)
            )
        );

        if (!string.IsNullOrEmpty(insuranceLineCode))
        {
            filter = builder.And(filter, builder.Eq(c => c.InsuranceLineCode, insuranceLineCode));
        }

        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<List<Coverage>> GetCoverageHistoryAsync(
        string tenantId,
        string memberId,
        bool includeTerminated = true)
    {
        var builder = Builders<Coverage>.Filter;
        var filter = builder.And(
            builder.Eq(c => c.TenantId, tenantId),
            builder.Eq(c => c.MemberId, memberId)
        );

        if (!includeTerminated)
        {
            filter = builder.And(filter, builder.Ne(c => c.Status, CoverageStatus.Terminated));
        }

        var sort = Builders<Coverage>.Sort.Descending(c => c.EffectiveDate);

        return await _collection.Find(filter).Sort(sort).ToListAsync();
    }

    public async Task<(IEnumerable<Coverage> Items, string? ContinuationToken)> SearchAsync(
        string tenantId,
        string? memberId = null,
        string? groupNumber = null,
        string? planId = null,
        bool activeOnly = false,
        int pageSize = 20,
        string? continuationToken = null)
    {
        var builder = Builders<Coverage>.Filter;
        var filter = builder.Eq(c => c.TenantId, tenantId);

        if (!string.IsNullOrEmpty(memberId))
        {
            filter = builder.And(filter, builder.Eq(c => c.MemberId, memberId));
        }

        if (!string.IsNullOrEmpty(groupNumber))
        {
            filter = builder.And(filter, builder.Eq(c => c.GroupNumber, groupNumber));
        }

        if (!string.IsNullOrEmpty(planId))
        {
            filter = builder.And(filter, builder.Eq(c => c.PlanId, planId));
        }

        if (activeOnly)
        {
            filter = builder.And(filter, builder.Eq(c => c.Status, CoverageStatus.Active));
        }

        // Pagination in MongoDB usually works with Skip/Limit.
        // ContinuationToken in Cosmos is different.
        // For simple migration, we'll assume integer skip if provided as token, or just 0.
        // WARNING: This is a simplified implementation. Real production apps might need cursor-based pagination.
        
        int skip = 0;
        if (!string.IsNullOrEmpty(continuationToken) && int.TryParse(continuationToken, out int tokenSkip))
        {
            skip = tokenSkip;
        }

        var results = await _collection.Find(filter)
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync();

        string? newToken = null;
        if (results.Count == pageSize)
        {
            newToken = (skip + pageSize).ToString();
        }

        return (results, newToken);
    }

    public async Task<List<Coverage>> GetByGroupNumberAsync(string tenantId, string groupNumber)
    {
        var filter = Builders<Coverage>.Filter.And(
            Builders<Coverage>.Filter.Eq(c => c.TenantId, tenantId),
            Builders<Coverage>.Filter.Eq(c => c.GroupNumber, groupNumber)
        );

        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<List<Coverage>> GetByPcpNpiAsync(
        string tenantId,
        string pcpNpi,
        CoverageStatus? status = null,
        LineOfBusiness? lineOfBusiness = null)
    {
        var builder = Builders<Coverage>.Filter;
        var filter = builder.And(
            builder.Eq(c => c.TenantId, tenantId),
            builder.Eq(c => c.PcpNpi, pcpNpi)
        );

        if (status.HasValue)
        {
            filter = builder.And(filter, builder.Eq(c => c.Status, status.Value));
        }

        if (lineOfBusiness.HasValue)
        {
            filter = builder.And(filter, builder.Eq(c => c.LineOfBusiness, lineOfBusiness.Value));
        }

        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<int> GetCountByGroupAsync(string tenantId, string groupNumber, CoverageStatus? status = null)
    {
        var builder = Builders<Coverage>.Filter;
        var filter = builder.And(
            builder.Eq(c => c.TenantId, tenantId),
            builder.Eq(c => c.GroupNumber, groupNumber)
        );

        if (status.HasValue)
        {
            filter = builder.And(filter, builder.Eq(c => c.Status, status.Value));
        }

        var count = await _collection.CountDocumentsAsync(filter);
        return (int)count;
    }

    public async Task<Coverage> CreateAsync(Coverage coverage)
    {
        coverage.CreatedDate = DateTime.UtcNow;
        coverage.LastUpdatedDate = DateTime.UtcNow;
        // Ensure ID is set if not already
        if (string.IsNullOrEmpty(coverage.Id))
        {
            coverage.Id = Guid.NewGuid().ToString();
        }

        await _collection.InsertOneAsync(coverage);
        return coverage;
    }

    public async Task<Coverage> UpdateAsync(Coverage coverage)
    {
        coverage.LastUpdatedDate = DateTime.UtcNow;

        var filter = Builders<Coverage>.Filter.And(
            Builders<Coverage>.Filter.Eq(c => c.TenantId, coverage.TenantId),
            Builders<Coverage>.Filter.Eq(c => c.Id, coverage.Id)
        );

        var result = await _collection.ReplaceOneAsync(filter, coverage);
        
        if (result.MatchedCount == 0)
        {
            throw new Exception($"Coverage with ID {coverage.Id} not found for update.");
        }

        return coverage;
    }

    public async Task DeleteAsync(string tenantId, string id)
    {
        var filter = Builders<Coverage>.Filter.And(
            Builders<Coverage>.Filter.Eq(c => c.TenantId, tenantId),
            Builders<Coverage>.Filter.Eq(c => c.Id, id)
        );

        await _collection.DeleteOneAsync(filter);
    }
}
