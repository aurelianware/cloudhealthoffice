using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Composes a claim intelligence read model from existing 837 / 277CA /
/// 276/277 / 275 / 835 stores. Refreshable and not a system of record.
/// </summary>
public interface IClaimIntelligenceComposer
{
    Task<ClaimIntelligenceView?> ComposeAsync(
        ClaimIntelligenceRequest request,
        CancellationToken cancellationToken = default);
}
