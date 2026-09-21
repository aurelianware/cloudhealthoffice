using AttachmentService.Models;

namespace AttachmentService.Services;

/// <summary>
/// Reads trading partner configuration for acknowledgment routing.
/// <para>
/// Split out of <see cref="AcknowledgmentService"/> so that acknowledgment generation stays
/// provider-agnostic: the lookup is the only part that touches a database, and the service runs
/// against MongoDB or Cosmos DB depending on which provider is configured.
/// </para>
/// </summary>
public interface ITradingPartnerLookup
{
    Task<TradingPartner?> GetByPayerIdAsync(string payerId, string tenantId);
}
