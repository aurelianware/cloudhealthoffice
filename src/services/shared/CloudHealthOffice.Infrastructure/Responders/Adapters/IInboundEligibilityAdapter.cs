using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Adapters;

/// <summary>
/// Inbound healthcare-transaction adapter for eligibility.
/// Translates an external format into
/// <see cref="PayerEligibilityInquiry"/> and a
/// <see cref="PayerEligibilityResponse"/> back into that format.
///
/// Adapters own vendor / X12 / FHIR mapping. They must not contain Cloud
/// Health Office member, benefit, accumulator, or network logic.
/// </summary>
public interface IInboundEligibilityAdapter
{
    /// <summary>Adapter name (e.g. "canonical", "x12", "stedi-planned").</summary>
    string Name { get; }

    /// <summary>
    /// True when this adapter can currently carry inbound 270-equivalent
    /// traffic. Planned vendor adapters return false until a real contract
    /// exists.
    /// </summary>
    bool IsImplemented { get; }
}

/// <summary>
/// Canonical JSON adapter: the payload <b>is</b> the CHO inquiry / response.
/// Used by the development direct API so the payer-side flow can be
/// exercised without a clearinghouse.
/// </summary>
public interface ICanonicalInboundEligibilityAdapter : IInboundEligibilityAdapter
{
    Task<GatewayResponse<PayerEligibilityResponse>> ProcessAsync(
        PayerEligibilityInquiry inquiry,
        CancellationToken ct = default);
}
