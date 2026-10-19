using AttachmentService.Models;

namespace AttachmentService.Services;

/// <summary>
/// Reads trading partner configuration for acknowledgment routing.
/// <para>
/// Split out of <see cref="AcknowledgmentService"/> so that acknowledgment generation stays
/// provider-agnostic: the lookup is the only part that touches another service's data.
/// </para>
/// </summary>
public interface ITradingPartnerLookup
{
    Task<TradingPartner?> GetByPayerIdAsync(string payerId, string tenantId);
}
