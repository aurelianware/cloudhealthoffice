using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Adapters;

public sealed class CanonicalInboundClaimAttachmentAdapter : ICanonicalInboundClaimAttachmentAdapter
{
    public const string AdapterName = "canonical";

    private readonly IClaimAttachmentReceiver _receiver;

    public CanonicalInboundClaimAttachmentAdapter(IClaimAttachmentReceiver receiver)
    {
        _receiver = receiver;
    }

    public string Name => AdapterName;

    public bool IsImplemented => true;

    public Task<GatewayResponse<InboundClaimAttachmentResult>> ProcessAsync(
        InboundClaimAttachment attachment,
        Stream content,
        CancellationToken ct = default)
    {
        attachment.AdapterName ??= AdapterName;
        attachment.Source ??= AdapterName;
        return _receiver.ReceiveAsync(attachment, content, ct);
    }
}
