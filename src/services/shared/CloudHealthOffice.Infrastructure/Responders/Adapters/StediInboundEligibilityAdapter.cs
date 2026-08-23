namespace CloudHealthOffice.Infrastructure.Responders.Adapters;

/// <summary>
/// Planned Stedi inbound adapter seam.
///
/// Stedi's current public Healthcare APIs (as of 2026-08-23) let a
/// <b>provider</b> submit 270 eligibility checks
/// (<c>POST /2024-04-01/change/medicalnetwork/eligibility/v3</c>) to
/// existing payers. Documented webhooks cover 277CA / 835 ERA and
/// enrollment events after a provider submits claims — not inbound 270
/// delivery to a custom payer endpoint.
///
/// There is no documented self-service mechanism for a core-administration
/// platform to register as a payer, receive inbound 270 JSON/X12, and
/// return a 271. This type exists so a future Stedi partnership can plug in
/// without changing <see cref="IEligibilityResponder"/>.
///
/// Status: <b>Adapter-ready / pending Stedi payer-side connectivity</b>.
/// Not implemented. Do not invent a Stedi inbound contract.
/// </summary>
public sealed class StediInboundEligibilityAdapter : IInboundEligibilityAdapter
{
    public const string AdapterName = "stedi-planned";

    public string Name => AdapterName;

    public bool IsImplemented => false;

    /// <summary>
    /// Always throws. Stedi does not currently expose a supported public
    /// inbound 270 payer-hosting API for Cloud Health Office to implement.
    /// </summary>
    public void EnsureImplemented() =>
        throw new NotSupportedException(
            "Stedi inbound 270/271 payer routing is not publicly available. " +
            "Cloud Health Office's payer-side responder is adapter-ready; " +
            "wire a real Stedi inbound contract here when one exists. " +
            "Until then use the canonical development adapter " +
            "(POST /api/dev/payer/eligibility).");
}
