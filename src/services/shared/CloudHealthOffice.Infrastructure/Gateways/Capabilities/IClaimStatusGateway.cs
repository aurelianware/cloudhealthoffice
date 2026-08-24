using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Capabilities;

/// <summary>
/// Gateway capability for 276/277 claim status inquiry.
///
/// A 276 asks an external payer what happened to a previously submitted claim.
/// The 277 response is not a 277CA acknowledgment, not adjudication, and not
/// an 835 remittance. Those remain separate lifecycle dimensions.
/// </summary>
public interface IClaimStatusGateway : IHealthcareTransactionGateway
{
    Task<GatewayResponse<ClaimStatusResponse>> CheckClaimStatusAsync(
        ClaimStatusRequest request,
        CancellationToken cancellationToken = default);
}
