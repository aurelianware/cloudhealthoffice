namespace CloudHealthOffice.Infrastructure.Gateways.Capabilities;

/// <summary>
/// Gateway capability for 835 electronic remittance advice.
///
/// <b>Foundation only.</b> The transaction method and canonical models are
/// intentionally deferred to a later PR (remittance is out of scope here).
/// The interface exists so gateways can advertise
/// <see cref="GatewayCapability.Remittance"/> without a no-op stub.
/// </summary>
public interface IRemittanceGateway : IHealthcareTransactionGateway
{
}
