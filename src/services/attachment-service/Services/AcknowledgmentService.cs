using AttachmentService.Models;

namespace AttachmentService.Services;

public class AcknowledgmentService : IAcknowledgmentService
{
    private readonly ITradingPartnerLookup _tradingPartners;
    private readonly AcknowledgmentGeneratorService _generator;
    private readonly ILogger<AcknowledgmentService> _logger;

    public AcknowledgmentService(
        ITradingPartnerLookup tradingPartners,
        AcknowledgmentGeneratorService generator,
        ILogger<AcknowledgmentService> logger)
    {
        _tradingPartners = tradingPartners;
        _generator = generator;
        _logger = logger;
    }

    public Task<string> Generate999Async(Attachment attachment, TradingPartner tradingPartner)
        => Task.FromResult(_generator.Generate999(attachment, tradingPartner));

    public Task<string> Generate824Async(Attachment attachment, TradingPartner tradingPartner)
        => Task.FromResult(_generator.Generate824(attachment, tradingPartner));

    public Task<TradingPartner?> GetTradingPartnerByPayerIdAsync(string payerId, string tenantId)
        => _tradingPartners.GetByPayerIdAsync(payerId, tenantId);

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
