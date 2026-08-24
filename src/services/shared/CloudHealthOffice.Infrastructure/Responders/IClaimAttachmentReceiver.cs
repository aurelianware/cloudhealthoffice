using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders;

/// <summary>
/// Payer-side inbound claim-attachment receiver: Cloud Health Office is the
/// payer that accepts a 275-equivalent document, matches it to an existing
/// payer-side claim, and stores it for examiner workflows.
///
/// Semantically the opposite of
/// <see cref="Gateways.Capabilities.IClaimAttachmentGateway"/>, which Cloud
/// Health Office uses to send attachments to an external payer.
/// </summary>
public interface IClaimAttachmentReceiver
{
    Task<GatewayResponse<InboundClaimAttachmentResult>> ReceiveAsync(
        InboundClaimAttachment attachment,
        Stream content,
        CancellationToken cancellationToken = default);
}
