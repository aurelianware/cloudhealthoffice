using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Default <see cref="IHealthcareGatewayResolver"/>. Resolves over the set of
/// <see cref="IHealthcareTransactionGateway"/> instances registered in DI,
/// matching by <see cref="IHealthcareTransactionGateway.Name"/>
/// (case-insensitive) and falling back to
/// <see cref="HealthcareTransactionOptions.DefaultGateway"/>.
/// </summary>
public sealed class HealthcareGatewayResolver : IHealthcareGatewayResolver
{
    private readonly IReadOnlyList<IHealthcareTransactionGateway> _gateways;
    private readonly HealthcareTransactionOptions _options;
    private readonly ILogger<HealthcareGatewayResolver> _logger;

    public HealthcareGatewayResolver(
        IEnumerable<IHealthcareTransactionGateway> gateways,
        IOptions<HealthcareTransactionOptions> options,
        ILogger<HealthcareGatewayResolver> logger)
    {
        _gateways = gateways.ToList();
        _options = options.Value;
        _logger = logger;
    }

    public IHealthcareTransactionGateway Resolve(string? name = null)
    {
        var target = string.IsNullOrWhiteSpace(name) ? _options.DefaultGateway : name;

        var gateway = _gateways.FirstOrDefault(g =>
            string.Equals(g.Name, target, StringComparison.OrdinalIgnoreCase));

        if (gateway is null)
        {
            var available = _gateways.Count == 0
                ? "(none registered)"
                : string.Join(", ", _gateways.Select(g => g.Name));
            throw new InvalidOperationException(
                $"No healthcare transaction gateway named '{target}' is registered. Available: {available}.");
        }

        return gateway;
    }

    public TCapability ResolveCapability<TCapability>(string? name = null)
        where TCapability : class, IHealthcareTransactionGateway
    {
        var gateway = Resolve(name);
        var capability = GatewayCapabilityMap.CapabilityFor(typeof(TCapability));

        // Reject explicitly when the gateway does not advertise the capability,
        // even if it happens to implement the (member-less) interface. This
        // keeps advertised capabilities and runtime behavior in agreement.
        if (capability is { } cap && !gateway.Supports(cap))
        {
            _logger.LogDebug(
                "Gateway {Gateway} does not support capability {Capability}",
                gateway.Name, cap);
            throw new GatewayCapabilityNotSupportedException(gateway.Name, cap);
        }

        if (gateway is not TCapability typed)
        {
            // Advertised the capability but does not implement its interface —
            // a registration bug rather than an unsupported transaction.
            throw new InvalidOperationException(
                $"Gateway '{gateway.Name}' advertises a capability for {typeof(TCapability).Name} " +
                "but does not implement that interface.");
        }

        return typed;
    }
}
