using AttachmentService.Models;
using Microsoft.Azure.Cosmos;

namespace AttachmentService.Services;

public class CosmosTradingPartnerLookup : ITradingPartnerLookup
{
    private readonly Container _container;
    private readonly ILogger<CosmosTradingPartnerLookup> _logger;

    public CosmosTradingPartnerLookup(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<CosmosTradingPartnerLookup> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:TradingPartnersContainerName"] ?? "TradingPartners";
        _container = cosmosClient.GetContainer(databaseName, containerName);
        _logger = logger;
    }

    public async Task<TradingPartner?> GetByPayerIdAsync(string payerId, string tenantId)
    {
        try
        {
            var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.partnerId = @payerId AND c.isActive = true")
                .WithParameter("@tenantId", tenantId)
                .WithParameter("@payerId", payerId);

            using var iterator = _container.GetItemQueryIterator<TradingPartner>(query);
            if (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                return response.FirstOrDefault();
            }

            return null;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "Trading partner not found for PayerId: {PayerId}, TenantId: {TenantId}",
                SanitizeForLog(payerId), SanitizeForLog(tenantId));
            return null;
        }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
