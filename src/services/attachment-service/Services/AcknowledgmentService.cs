using AttachmentService.Models;
using Microsoft.Azure.Cosmos;

namespace AttachmentService.Services;

public class AcknowledgmentService : IAcknowledgmentService
{
    private readonly Container _tradingPartnersContainer;
    private readonly AcknowledgmentGeneratorService _generator;
    private readonly ILogger<AcknowledgmentService> _logger;

    public AcknowledgmentService(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        AcknowledgmentGeneratorService generator,
        ILogger<AcknowledgmentService> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:TradingPartnersContainerName"] ?? "TradingPartners";
        _tradingPartnersContainer = cosmosClient.GetContainer(databaseName, containerName);
        _generator = generator;
        _logger = logger;
    }

    public Task<string> Generate999Async(Attachment attachment, TradingPartner tradingPartner)
        => Task.FromResult(_generator.Generate999(attachment, tradingPartner));

    public Task<string> Generate824Async(Attachment attachment, TradingPartner tradingPartner)
        => Task.FromResult(_generator.Generate824(attachment, tradingPartner));

    public async Task<TradingPartner?> GetTradingPartnerByPayerIdAsync(string payerId, string tenantId)
    {
        try
        {
            var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.partnerId = @payerId AND c.isActive = true")
                .WithParameter("@tenantId", tenantId)
                .WithParameter("@payerId", payerId);

            using var iterator = _tradingPartnersContainer.GetItemQueryIterator<TradingPartner>(query);
            if (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                return response.FirstOrDefault();
            }

            return null;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Trading partner not found for PayerId: {PayerId}, TenantId: {TenantId}", SanitizeForLog(payerId), SanitizeForLog(tenantId));
            return null;
        }
    }

    public string GetAcknowledgmentType(TradingPartner? tradingPartner)
    {
        // Default to 999 if no trading partner config found
        return tradingPartner?.AttachmentAckType ?? "999";
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
