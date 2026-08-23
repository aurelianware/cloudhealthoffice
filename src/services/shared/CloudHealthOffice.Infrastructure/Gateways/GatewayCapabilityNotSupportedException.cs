namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Thrown when a caller requests a <see cref="GatewayCapability"/> from a
/// gateway that does not implement it. Making the rejection explicit — rather
/// than returning a fake/empty result — keeps unsupported transactions
/// visible to callers and tests.
/// </summary>
public sealed class GatewayCapabilityNotSupportedException : InvalidOperationException
{
    /// <summary>The gateway that was asked for an unsupported capability.</summary>
    public string GatewayName { get; }

    /// <summary>The capability that is not supported.</summary>
    public GatewayCapability Capability { get; }

    public GatewayCapabilityNotSupportedException(string gatewayName, GatewayCapability capability)
        : base($"Gateway '{gatewayName}' does not support the '{capability}' capability.")
    {
        GatewayName = gatewayName;
        Capability = capability;
    }
}
