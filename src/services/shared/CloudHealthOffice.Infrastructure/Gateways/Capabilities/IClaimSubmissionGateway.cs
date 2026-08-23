namespace CloudHealthOffice.Infrastructure.Gateways.Capabilities;

/// <summary>
/// Gateway capability for 837 claim submission (837P professional, 837I
/// institutional, 837D dental).
///
/// <b>Foundation only.</b> The transaction method and its canonical
/// request/response models are intentionally not defined in this release —
/// claim submission is out of scope and will be added in a later PR. The
/// interface exists so a gateway can advertise
/// <see cref="GatewayCapability.ClaimSubmission"/> and so the capability
/// surface is discoverable now. It is deliberately left without members
/// rather than carrying a no-op method.
/// </summary>
public interface IClaimSubmissionGateway : IHealthcareTransactionGateway
{
}
