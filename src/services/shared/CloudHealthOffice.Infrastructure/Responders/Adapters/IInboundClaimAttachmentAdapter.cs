using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Adapters;

/// <summary>
/// Translates an external inbound 275 format into
/// <see cref="InboundClaimAttachment"/>. Adapters must not match claims or
/// run examiner logic.
/// </summary>
public interface IInboundClaimAttachmentAdapter
{
    string Name { get; }

    bool IsImplemented { get; }
}

public interface ICanonicalInboundClaimAttachmentAdapter : IInboundClaimAttachmentAdapter
{
    Task<GatewayResponse<InboundClaimAttachmentResult>> ProcessAsync(
        InboundClaimAttachment attachment,
        Stream content,
        CancellationToken ct = default);
}
