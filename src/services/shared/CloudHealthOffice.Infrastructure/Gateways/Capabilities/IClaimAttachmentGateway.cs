using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Capabilities;

/// <summary>
/// Gateway capability for 275 claim attachments / additional information.
///
/// A 275 is supporting documentation for a claim or related transaction. It
/// is not a claim, acknowledgment, adjudication result, or payment.
/// </summary>
public interface IClaimAttachmentGateway : IHealthcareTransactionGateway
{
    Task<GatewayResponse<ClaimAttachmentSubmissionResult>> SubmitAttachmentAsync(
        ClaimAttachmentSubmissionRequest request,
        CancellationToken cancellationToken = default);
}
