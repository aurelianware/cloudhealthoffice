using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Capabilities;

/// <summary>
/// Gateway capability for outbound 837 claim submission (837P professional,
/// 837I institutional, 837D dental).
///
/// Cloud Health Office owns claim/adjudication business logic. This gateway
/// carries a canonical submission projection to an external payer/clearinghouse
/// and returns a transmission result. A successful HTTP submission is
/// <b>not</b> a 277CA, adjudication, or payment.
/// </summary>
public interface IClaimSubmissionGateway : IHealthcareTransactionGateway
{
    Task<GatewayResponse<GatewayClaimSubmissionResult>> SubmitClaimAsync(
        GatewayClaimSubmissionRequest request, CancellationToken ct = default);
}
