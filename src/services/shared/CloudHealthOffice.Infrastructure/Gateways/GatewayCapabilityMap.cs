using CloudHealthOffice.Infrastructure.Gateways.Capabilities;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Bidirectional mapping between a <see cref="GatewayCapability"/> and the
/// capability-specific interface a gateway must implement to advertise it.
///
/// Kept in one place so capability discovery and resolver rejection stay in
/// lock-step with the interface set. When a new capability/interface pair is
/// introduced, extend <see cref="InterfaceByCapability"/> — the resolver and
/// registration code read from here rather than hard-coding type checks.
/// </summary>
public static class GatewayCapabilityMap
{
    private static readonly IReadOnlyDictionary<GatewayCapability, Type> InterfaceByCapability =
        new Dictionary<GatewayCapability, Type>
        {
            [GatewayCapability.Eligibility] = typeof(IEligibilityGateway),
            [GatewayCapability.ClaimSubmission] = typeof(IClaimSubmissionGateway),
            [GatewayCapability.ClaimStatus] = typeof(IClaimStatusGateway),
            [GatewayCapability.ClaimAcknowledgment] = typeof(IClaimAcknowledgmentGateway),
            [GatewayCapability.ClaimAttachment] = typeof(IClaimAttachmentGateway),
            [GatewayCapability.Remittance] = typeof(IRemittanceGateway)
        };

    /// <summary>The capability-specific interface type for a capability.</summary>
    public static Type InterfaceFor(GatewayCapability capability) =>
        InterfaceByCapability.TryGetValue(capability, out var type)
            ? type
            : throw new ArgumentOutOfRangeException(
                nameof(capability), capability, "No interface is mapped for this capability.");

    /// <summary>
    /// The capability advertised by a capability-specific interface type, or
    /// <c>null</c> when <paramref name="interfaceType"/> is not a mapped
    /// capability interface.
    /// </summary>
    public static GatewayCapability? CapabilityFor(Type interfaceType)
    {
        foreach (var pair in InterfaceByCapability)
        {
            if (pair.Value == interfaceType)
            {
                return pair.Key;
            }
        }

        return null;
    }
}
