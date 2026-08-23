using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders;

/// <summary>
/// Payer-side eligibility responder: Cloud Health Office is the information
/// source that answers an inbound 270-equivalent inquiry with a 271-equivalent
/// response.
///
/// Semantically the opposite of
/// <see cref="Gateways.Capabilities.IEligibilityGateway"/>, which Cloud Health
/// Office uses to ask an external payer. Do not overload the gateway with this
/// inbound responsibility.
///
/// <code>
/// EligibilityInquiryClient / IEligibilityGateway
///     CHO → external payer
///
/// IEligibilityResponder
///     external provider → CHO
/// </code>
///
/// The responder is vendor-neutral and read-only. It does not persist
/// inquiries, create claims, consume accumulators, or change enrollment.
/// </summary>
public interface IEligibilityResponder
{
    /// <summary>
    /// Evaluate an inbound eligibility inquiry against Cloud Health Office
    /// member, coverage, benefit, network, and accumulator data.
    ///
    /// The envelope's <see cref="GatewayResponse{TResult}.IsSuccess"/> reflects
    /// <b>transport</b> success. Business rejections (member not found, inactive
    /// coverage, unsupported service type) are represented on
    /// <see cref="PayerEligibilityResponse.BusinessStatus"/> with
    /// <c>IsSuccess = true</c>.
    /// </summary>
    Task<GatewayResponse<PayerEligibilityResponse>> RespondAsync(
        PayerEligibilityInquiry inquiry,
        CancellationToken ct = default);
}
