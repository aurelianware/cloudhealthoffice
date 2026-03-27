using EligibilityService.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EligibilityService.Repositories;

/// <summary>
/// MongoDB implementation of the Eligibility Repository.
/// Handles both Inquiries and Responses using separate collections.
/// </summary>
public class EligibilityRepositoryMongo : IEligibilityRepository
{
    private readonly IMongoCollection<EligibilityInquiry> _inquiryCollection;
    private readonly IMongoCollection<EligibilityResponse> _responseCollection;
    private readonly ILogger<EligibilityRepositoryMongo> _logger;

    public EligibilityRepositoryMongo(IMongoDatabase database, ILogger<EligibilityRepositoryMongo> logger)
    {
        _inquiryCollection = database.GetCollection<EligibilityInquiry>("EligibilityInquiries");
        _responseCollection = database.GetCollection<EligibilityResponse>("EligibilityResponses");
        _logger = logger;
        
        // Ensure indexes
        CreateIndexes();
    }

    private void CreateIndexes()
    {
        // Inquiry Indexes
        var inquiryIndexKeys = Builders<EligibilityInquiry>.IndexKeys;
        var inquiryIndexModels = new List<CreateIndexModel<EligibilityInquiry>>
        {
            new CreateIndexModel<EligibilityInquiry>(inquiryIndexKeys.Ascending(c => c.TenantId).Ascending(c => c.ControlNumber)),
            new CreateIndexModel<EligibilityInquiry>(inquiryIndexKeys.Ascending(c => c.TenantId).Ascending(c => c.SubscriberId).Descending(c => c.CreatedDate))
        };
        _inquiryCollection.Indexes.CreateMany(inquiryIndexModels);

        // Response Indexes
        var responseIndexKeys = Builders<EligibilityResponse>.IndexKeys;
        var responseIndexModels = new List<CreateIndexModel<EligibilityResponse>>
        {
            new CreateIndexModel<EligibilityResponse>(responseIndexKeys.Ascending(c => c.TenantId).Ascending(c => c.InquiryId))
        };
        _responseCollection.Indexes.CreateMany(responseIndexModels);
    }

    // Inquiry Methods

    public async Task<EligibilityInquiry?> GetInquiryByIdAsync(string tenantId, string id)
    {
        var filter = Builders<EligibilityInquiry>.Filter.And(
            Builders<EligibilityInquiry>.Filter.Eq(c => c.TenantId, tenantId),
            Builders<EligibilityInquiry>.Filter.Eq(c => c.Id, id)
        );

        return await _inquiryCollection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<EligibilityInquiry?> GetInquiryByControlNumberAsync(string tenantId, string controlNumber)
    {
        var filter = Builders<EligibilityInquiry>.Filter.And(
            Builders<EligibilityInquiry>.Filter.Eq(c => c.TenantId, tenantId),
            Builders<EligibilityInquiry>.Filter.Eq(c => c.ControlNumber, controlNumber)
        );

        return await _inquiryCollection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<List<EligibilityInquiry>> GetInquiriesBySubscriberAsync(
        string tenantId, string subscriberId, int page, int pageSize)
    {
        var filter = Builders<EligibilityInquiry>.Filter.And(
            Builders<EligibilityInquiry>.Filter.Eq(c => c.TenantId, tenantId),
            Builders<EligibilityInquiry>.Filter.Eq(c => c.SubscriberId, subscriberId)
        );

        return await _inquiryCollection.Find(filter)
            .SortByDescending(c => c.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task CreateInquiryAsync(EligibilityInquiry inquiry)
    {
        if (string.IsNullOrEmpty(inquiry.Id))
        {
            inquiry.Id = Guid.NewGuid().ToString();
        }
        
        if (inquiry.CreatedDate == default)
        {
            inquiry.CreatedDate = DateTime.UtcNow;
        }
        
        await _inquiryCollection.InsertOneAsync(inquiry);
        _logger.LogInformation("Created eligibility inquiry {InquiryId} (Mongo)", SanitizeForLog(inquiry.Id));
    }

    public async Task UpdateInquiryAsync(EligibilityInquiry inquiry)
    {
        inquiry.ModifiedDate = DateTime.UtcNow;
        
        var filter = Builders<EligibilityInquiry>.Filter.And(
            Builders<EligibilityInquiry>.Filter.Eq(c => c.TenantId, inquiry.TenantId),
            Builders<EligibilityInquiry>.Filter.Eq(c => c.Id, inquiry.Id)
        );

        var result = await _inquiryCollection.ReplaceOneAsync(filter, inquiry);
        
        if (result.MatchedCount == 0)
        {
            // If it doesn't exist, we might want to insert via Update policy, or throw.
            // Following repository semantics (UpsertItemAsync usually implies create if not exists).
            await _inquiryCollection.InsertOneAsync(inquiry);
        }
        
        _logger.LogInformation("Updated eligibility inquiry {InquiryId} (Mongo)", SanitizeForLog(inquiry.Id));
    }

    // Response Methods

    public async Task<EligibilityResponse?> GetResponseByIdAsync(string tenantId, string id)
    {
        var filter = Builders<EligibilityResponse>.Filter.And(
            Builders<EligibilityResponse>.Filter.Eq(c => c.TenantId, tenantId),
            Builders<EligibilityResponse>.Filter.Eq(c => c.Id, id)
        );

        return await _responseCollection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<EligibilityResponse?> GetResponseByInquiryIdAsync(string tenantId, string inquiryId)
    {
        var filter = Builders<EligibilityResponse>.Filter.And(
            Builders<EligibilityResponse>.Filter.Eq(c => c.TenantId, tenantId),
            Builders<EligibilityResponse>.Filter.Eq(c => c.InquiryId, inquiryId)
        );

        return await _responseCollection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task CreateResponseAsync(EligibilityResponse response)
    {
         if (string.IsNullOrEmpty(response.Id))
        {
            response.Id = Guid.NewGuid().ToString();
        }
        
        await _responseCollection.InsertOneAsync(response);
        
        _logger.LogInformation("Created eligibility response {ResponseId} for inquiry {InquiryId} (Mongo)", 
            SanitizeForLog(response.Id), SanitizeForLog(response.InquiryId));
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Remove newline characters to prevent log forging via user-controlled data.
        return value.Replace("\r", string.Empty)
                    .Replace("\n", string.Empty);
    }
}
