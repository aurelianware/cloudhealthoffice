namespace CloudHealthOffice.Infrastructure.Gateways.Capabilities;

/// <summary>
/// Gateway capability for 275 claim attachments / additional information.
///
/// <b>Foundation only.</b> The transaction method and canonical models are
/// intentionally deferred to a later PR (attachments are out of scope here).
/// The interface exists so gateways can advertise
/// <see cref="GatewayCapability.ClaimAttachment"/> without a no-op stub.
/// </summary>
public interface IClaimAttachmentGateway : IHealthcareTransactionGateway
{
}
