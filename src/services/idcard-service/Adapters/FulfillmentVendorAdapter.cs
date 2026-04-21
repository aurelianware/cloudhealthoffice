namespace IdCardService.Adapters;

/// <summary>
/// Placeholder for the Phase-2 external physical-card mail vendor. Registered
/// so tenant configuration referencing <c>"fulfillment-vendor"</c> resolves
/// cleanly with a controlled error rather than silently falling back to CHO.
/// </summary>
public class FulfillmentVendorAdapter : IIdCardAdapter
{
    public string Platform => "fulfillment-vendor";

    public Task<IdCardIssueResult> IssueAsync(IdCardIssueRequest request, CancellationToken ct = default)
    {
        // TODO (Phase 2): wire to vendor API (address, template id, tracking webhook).
        throw new NotSupportedException(
            "fulfillment-vendor adapter is Phase 2 (physical mail). Configure 'cho' or 'qnxt' for Phase 1.");
    }
}
