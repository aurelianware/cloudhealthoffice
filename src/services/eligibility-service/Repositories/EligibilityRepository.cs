using Microsoft.Azure.Cosmos;
using EligibilityService.Models;

namespace EligibilityService.Repositories;

public class EligibilityRepository : IEligibilityRepository
{
    private readonly Container _inquiryContainer;
    private readonly Container _responseContainer;
    private readonly ILogger<EligibilityRepository> _logger;

    public EligibilityRepository(CosmosClient cosmosClient, ILogger<EligibilityRepository> logger)
    {
        var database = cosmosClient.GetDatabase("CloudHealthOffice");
        _inquiryContainer = database.GetContainer("EligibilityInquiries");
        _responseContainer = database.GetContainer("EligibilityResponses");
        _logger = logger;
    }

    public async Task<EligibilityInquiry?> GetInquiryByIdAsync(string tenantId, string id)
    {
        try
        {
            var response = await _inquiryContainer.ReadItemAsync<EligibilityInquiry>(
                id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<EligibilityInquiry?> GetInquiryByControlNumberAsync(string tenantId, string controlNumber)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.controlNumber = @controlNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@controlNumber", controlNumber);

        var iterator = _inquiryContainer.GetItemQueryIterator<EligibilityInquiry>(query);
        
        if (iterator.HasMoreResults)
        {
            var results = await iterator.ReadNextAsync();
            return results.FirstOrDefault();
        }

        return null;
    }

    public async Task<List<EligibilityInquiry>> GetInquiriesBySubscriberAsync(
        string tenantId, string subscriberId, int page, int pageSize)
    {
        var offset = (page - 1) * pageSize;
        
        var query = new QueryDefinition(
            @"SELECT * FROM c 
              WHERE c.tenantId = @tenantId 
              AND c.subscriberId = @subscriberId 
              ORDER BY c.createdDate DESC 
              OFFSET @offset LIMIT @limit")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@subscriberId", subscriberId)
            .WithParameter("@offset", offset)
            .WithParameter("@limit", pageSize);

        var results = new List<EligibilityInquiry>();
        var iterator = _inquiryContainer.GetItemQueryIterator<EligibilityInquiry>(query);
        
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task CreateInquiryAsync(EligibilityInquiry inquiry)
    {
        inquiry.Id = Guid.NewGuid().ToString();
        inquiry.CreatedDate = DateTime.UtcNow;
        
        await _inquiryContainer.CreateItemAsync(inquiry, new PartitionKey(inquiry.TenantId));
        
        _logger.LogInformation("Created eligibility inquiry {InquiryId}", SanitizeForLog(inquiry.Id));
    }

    public async Task UpdateInquiryAsync(EligibilityInquiry inquiry)
    {
        inquiry.ModifiedDate = DateTime.UtcNow;
        
        await _inquiryContainer.UpsertItemAsync(inquiry, new PartitionKey(inquiry.TenantId));
        
        _logger.LogInformation("Updated eligibility inquiry {InquiryId}", SanitizeForLog(inquiry.Id));
    }

    public async Task<EligibilityResponse?> GetResponseByIdAsync(string tenantId, string id)
    {
        try
        {
            var response = await _responseContainer.ReadItemAsync<EligibilityResponse>(
                id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<EligibilityResponse?> GetResponseByInquiryIdAsync(string tenantId, string inquiryId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.inquiryId = @inquiryId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@inquiryId", inquiryId);

        var iterator = _responseContainer.GetItemQueryIterator<EligibilityResponse>(query);
        
        if (iterator.HasMoreResults)
        {
            var results = await iterator.ReadNextAsync();
            return results.FirstOrDefault();
        }

        return null;
    }

    public async Task CreateResponseAsync(EligibilityResponse response)
    {
        await _responseContainer.CreateItemAsync(response, new PartitionKey(response.TenantId));
        
        _logger.LogInformation("Created eligibility response {ResponseId} for inquiry {InquiryId}", 
            SanitizeForLog(response.Id), SanitizeForLog(response.InquiryId));
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
