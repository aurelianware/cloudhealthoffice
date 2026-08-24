namespace CloudHealthOffice.Infrastructure.Responders.Adapters;

/// <summary>
/// Planned Stedi inbound 275 adapter seam.
///
/// Stedi's current public Healthcare APIs (reviewed 2026-08-24) let a
/// <b>provider</b> submit unsolicited 275s
/// (<c>POST https://claims.us.stedi.com/2025-03-07/claim-attachments/file</c>
/// and raw X12 submission) <b>to</b> an existing payer. Documented webhooks
/// cover 277CA and 835 after a provider submitted an 837 — not inbound 275
/// delivery to a custom payer application.
///
/// There is no documented self-service mechanism for a core-administration
/// platform to register as a payer, receive inbound 275 JSON/X12, or retrieve
/// inbound attachment binaries as the receiving trading partner.
///
/// Status: <b>Adapter-ready / pending Stedi payer-side connectivity</b>.
/// Not implemented. Do not invent a Stedi inbound contract.
/// </summary>
public sealed class StediInboundClaimAttachmentAdapter : IInboundClaimAttachmentAdapter
{
    public const string AdapterName = "stedi-planned";

    public string Name => AdapterName;

    public bool IsImplemented => false;

    public void EnsureImplemented() =>
        throw new NotSupportedException(
            "Stedi inbound payer-side 275 routing is not publicly available. " +
            "Cloud Health Office's payer-side receiver is adapter-ready; " +
            "wire a real Stedi inbound contract here when one exists. " +
            "Until then use the canonical development adapter " +
            "(POST /api/dev/payer/claims/{claimId}/attachments).");
}
