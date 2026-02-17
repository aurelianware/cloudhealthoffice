using AttachmentService.Models;
using Microsoft.Azure.Cosmos;

namespace AttachmentService.Services;

public class AcknowledgmentService : IAcknowledgmentService
{
    private readonly Container _tradingPartnersContainer;
    private readonly ILogger<AcknowledgmentService> _logger;

    public AcknowledgmentService(CosmosClient cosmosClient, IConfiguration configuration, ILogger<AcknowledgmentService> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:TradingPartnersContainerName"] ?? "TradingPartners";
        _tradingPartnersContainer = cosmosClient.GetContainer(databaseName, containerName);
        _logger = logger;
    }

    public async Task<string> Generate999Async(Attachment attachment, TradingPartner tradingPartner)
    {
        // Generate 999 Implementation Acknowledgment
        var now = DateTime.UtcNow;
        var controlNumber = GenerateControlNumber();

        var isa = $"ISA*00*          *00*          *ZZ*{tradingPartner.InterchangeSenderId?.PadRight(15) ?? "SENDER".PadRight(15)}*ZZ*{tradingPartner.InterchangeReceiverId?.PadRight(15) ?? "RECEIVER".PadRight(15)}*{now:yyMMdd}*{now:HHmm}*^*00501*{controlNumber}*0*P*:~";
        var gs = $"GS*FA*{tradingPartner.ApplicationSenderId ?? "SENDER"}*{tradingPartner.ApplicationReceiverId ?? "RECEIVER"}*{now:yyyyMMdd}*{now:HHmm}*1*X*005010~";
        var st = "ST*999*0001*005010~";
        var ak1 = "AK1*HS*1~"; // HS = Health Care Services (275)
        var ak2 = $"AK2*275*{attachment.Id.Substring(0, Math.Min(9, attachment.Id.Length))}~";
        var ak5 = "AK5*A~"; // A = Accepted
        var ak9 = "AK9*A*1*1*1~"; // Accepted, 1 group, 1 transaction, 1 accepted
        var se = "SE*6*0001~";
        var ge = "GE*1*1~";
        var iea = $"IEA*1*{controlNumber}~";

        return $"{isa}{gs}{st}{ak1}{ak2}{ak5}{ak9}{se}{ge}{iea}";
    }

    public async Task<string> Generate824Async(Attachment attachment, TradingPartner tradingPartner)
    {
        // Generate 824 Application Advice
        var now = DateTime.UtcNow;
        var controlNumber = GenerateControlNumber();

        var isa = $"ISA*00*          *00*          *ZZ*{tradingPartner.InterchangeSenderId?.PadRight(15) ?? "SENDER".PadRight(15)}*ZZ*{tradingPartner.InterchangeReceiverId?.PadRight(15) ?? "RECEIVER".PadRight(15)}*{now:yyMMdd}*{now:HHmm}*^*00501*{controlNumber}*0*P*:~";
        var gs = $"GS*AG*{tradingPartner.ApplicationSenderId ?? "SENDER"}*{tradingPartner.ApplicationReceiverId ?? "RECEIVER"}*{now:yyyyMMdd}*{now:HHmm}*1*X*005010~";
        var st = "ST*824*0001*005010~";
        var bgs = $"BGN*11*{attachment.Id}*{now:yyyyMMdd}*{now:HHmmss}~";
        
        // Determine acceptance code based on status
        var acceptanceCode = attachment.Status switch
        {
            "Linked" => "TA", // Accepted
            "Validated" => "TA", // Accepted
            "Failed" => "TR", // Rejected
            _ => "TP" // Partially Accepted/Pending
        };

        var oti = $"OTI*TA*TN*{attachment.Id}~"; // Transaction Information
        var ref1 = $"REF*D9*{attachment.Id}~"; // Claim Number Reference
        
        var msgText = attachment.Status switch
        {
            "Linked" => $"Attachment accepted and linked to {GetParentType(attachment)} {GetParentId(attachment)}",
            "Validated" => "Attachment accepted and validated",
            "Failed" => $"Attachment rejected: {attachment.Notes}",
            _ => "Attachment received and pending validation"
        };

        var msg = $"MSG*{msgText}~";
        
        var se = "SE*6*0001~";
        var ge = "GE*1*1~";
        var iea = $"IEA*1*{controlNumber}~";

        return $"{isa}{gs}{st}{bgs}{oti}{ref1}{msg}{se}{ge}{iea}";
    }

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
            _logger.LogWarning("Trading partner not found for PayerId: {PayerId}, TenantId: {TenantId}", payerId, tenantId);
            return null;
        }
    }

    public string GetAcknowledgmentType(TradingPartner? tradingPartner)
    {
        // Default to 999 if no trading partner config found
        return tradingPartner?.AttachmentAckType ?? "999";
    }

    private string GenerateControlNumber()
    {
        return DateTime.UtcNow.Ticks.ToString().Substring(9, 9);
    }

    private string GetParentType(Attachment attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.ClaimId)) return "Claim";
        if (!string.IsNullOrWhiteSpace(attachment.AuthorizationId)) return "Authorization";
        if (!string.IsNullOrWhiteSpace(attachment.AppealId)) return "Appeal";
        return "Unknown";
    }

    private string GetParentId(Attachment attachment)
    {
        return attachment.ClaimId ?? attachment.AuthorizationId ?? attachment.AppealId ?? "Unknown";
    }
}
