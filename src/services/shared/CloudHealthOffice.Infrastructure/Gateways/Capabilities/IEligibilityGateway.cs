using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Capabilities;

/// <summary>
/// Gateway capability for 270/271 eligibility &amp; benefit inquiry.
///
/// Implemented by gateways that can carry an eligibility transaction to an
/// external payer/clearinghouse and translate the response back into the
/// Cloud Health Office canonical <see cref="GatewayEligibilityResponse"/>.
/// The request and response are vendor-neutral: no Stedi/Availity/X12 types
/// cross this boundary.
/// </summary>
public interface IEligibilityGateway : IHealthcareTransactionGateway
{
    /// <summary>
    /// Verify member eligibility for the given request and return a normalized
    /// Cloud Health Office response wrapped in a metadata-bearing envelope.
    /// </summary>
    Task<GatewayResponse<GatewayEligibilityResponse>> CheckEligibilityAsync(
        GatewayEligibilityRequest request, CancellationToken ct = default);
}
