using AttachmentService.Models;

namespace AttachmentService.Services;

public interface IAcknowledgmentService
{
    /// <summary>
    /// Generate 999 Implementation Acknowledgment for a 275 attachment
    /// </summary>
    Task<string> Generate999Async(Attachment attachment, TradingPartner tradingPartner);

    /// <summary>
    /// Generate 824 Application Advice for a 275 attachment
    /// </summary>
    Task<string> Generate824Async(Attachment attachment, TradingPartner tradingPartner);

    /// <summary>
    /// Get trading partner configuration by payer ID
    /// </summary>
    Task<TradingPartner?> GetTradingPartnerByPayerIdAsync(string payerId, string tenantId);

    /// <summary>
    /// Determine which acknowledgment type(s) to send based on trading partner config
    /// </summary>
    string GetAcknowledgmentType(TradingPartner? tradingPartner);
}
