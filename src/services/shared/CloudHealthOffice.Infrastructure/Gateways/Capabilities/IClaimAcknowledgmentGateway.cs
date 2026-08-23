namespace CloudHealthOffice.Infrastructure.Gateways.Capabilities;

/// <summary>
/// Gateway capability for 277CA claim acknowledgment.
///
/// <b>Foundation only.</b> The transaction method and canonical models are
/// intentionally deferred to a later PR (claim acknowledgment is out of scope
/// here). The interface exists so gateways can advertise
/// <see cref="GatewayCapability.ClaimAcknowledgment"/> without a no-op stub.
/// </summary>
public interface IClaimAcknowledgmentGateway : IHealthcareTransactionGateway
{
}
