namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Vendor-neutral abstraction for external healthcare transaction transport.
///
/// A gateway carries HIPAA/X12 transactions between Cloud Health Office and an
/// external payer or clearinghouse (Stedi, Availity, a direct payer link, or a
/// direct X12 / FHIR adapter). Cloud Health Office owns all business
/// interpretation — eligibility/benefit logic, member coverage, provider and
/// network rules, claims adjudication, pricing, and accumulators. A gateway
/// owns only transport and vendor-specific translation to and from Cloud
/// Health Office canonical models.
///
/// Not every gateway implements every transaction. A gateway advertises what
/// it supports via <see cref="Capabilities"/> and implements the matching
/// capability-specific interfaces
/// (<see cref="Capabilities.IEligibilityGateway"/> and friends). Unsupported
/// transactions are discovered explicitly through capability checks rather
/// than by calling a no-op stub.
/// </summary>
public interface IHealthcareTransactionGateway
{
    /// <summary>
    /// Stable logical name for this gateway (e.g. "Mock", "Stedi", "Availity").
    /// Matches the configured <c>HealthcareTransactions:DefaultGateway</c> value
    /// and the per-gateway configuration key.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The set of capabilities this gateway actually implements. Must be
    /// consistent with the capability-specific interfaces the gateway
    /// implements.
    /// </summary>
    IReadOnlySet<GatewayCapability> Capabilities { get; }

    /// <summary>True when this gateway implements <paramref name="capability"/>.</summary>
    bool Supports(GatewayCapability capability) => Capabilities.Contains(capability);
}
