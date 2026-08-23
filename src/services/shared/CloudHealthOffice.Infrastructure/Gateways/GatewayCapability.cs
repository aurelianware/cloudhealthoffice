namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Coarse-grained capabilities a healthcare transaction gateway may advertise.
///
/// A capability maps to one of the capability-specific interfaces
/// (<see cref="Capabilities.IEligibilityGateway"/>,
/// <see cref="Capabilities.IClaimSubmissionGateway"/>, etc.). A gateway
/// declares only the capabilities it actually implements — unsupported
/// transactions stay explicit rather than becoming no-op stubs.
///
/// To add a new capability: add a member here, add the matching
/// capability-specific interface, and map it in
/// <see cref="GatewayCapabilityMap"/>.
/// </summary>
public enum GatewayCapability
{
    /// <summary>270/271 eligibility &amp; benefit inquiry.</summary>
    Eligibility,

    /// <summary>837 claim submission (professional / institutional / dental).</summary>
    ClaimSubmission,

    /// <summary>276/277 claim status inquiry.</summary>
    ClaimStatus,

    /// <summary>277CA claim acknowledgment.</summary>
    ClaimAcknowledgment,

    /// <summary>275 claim attachments.</summary>
    ClaimAttachment,

    /// <summary>835 electronic remittance advice.</summary>
    Remittance
}
