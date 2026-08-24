using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Capabilities;

/// <summary>
/// Gateway capability for retrieving a 277CA claim acknowledgment.
///
/// Stedi (and similar clearinghouses) deliver 277CAs asynchronously: a webhook
/// or poll item is a pointer, and this method retrieves the normalized
/// acknowledgment. Applying it to a transmission is
/// <see cref="IClaimAcknowledgmentProcessor"/> — not this interface.
/// </summary>
public interface IClaimAcknowledgmentGateway : IHealthcareTransactionGateway
{
    Task<GatewayResponse<GatewayClaimAcknowledgment>> RetrieveAcknowledgmentAsync(
        ClaimAcknowledgmentRetrievalRequest request,
        CancellationToken cancellationToken = default);
}
