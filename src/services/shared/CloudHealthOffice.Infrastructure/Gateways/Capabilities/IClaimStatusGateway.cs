namespace CloudHealthOffice.Infrastructure.Gateways.Capabilities;

/// <summary>
/// Gateway capability for 276/277 claim status inquiry.
///
/// <b>Foundation only.</b> The transaction method and canonical models are
/// intentionally deferred to a later PR (claim status is out of scope here).
/// The interface exists so gateways can advertise
/// <see cref="GatewayCapability.ClaimStatus"/> without a no-op stub.
/// </summary>
public interface IClaimStatusGateway : IHealthcareTransactionGateway
{
}
