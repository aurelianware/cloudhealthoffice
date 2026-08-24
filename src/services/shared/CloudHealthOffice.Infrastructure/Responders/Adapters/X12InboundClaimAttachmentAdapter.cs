namespace CloudHealthOffice.Infrastructure.Responders.Adapters;

/// <summary>
/// Deferred raw X12 275 ingress seam. CHO has 275 XSD/appeal consumers but
/// no reusable payer-side 275 parser in this assembly. A future PR can map
/// 005010X210 into <c>InboundClaimAttachment</c> without changing
/// <see cref="IClaimAttachmentReceiver"/>.
/// </summary>
public sealed class X12InboundClaimAttachmentAdapter : IInboundClaimAttachmentAdapter
{
    public const string AdapterName = "x12-planned";

    public string Name => AdapterName;

    public bool IsImplemented => false;
}
