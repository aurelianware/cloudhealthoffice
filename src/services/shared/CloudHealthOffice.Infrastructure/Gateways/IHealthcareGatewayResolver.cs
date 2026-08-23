namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Resolves a configured <see cref="IHealthcareTransactionGateway"/> by name
/// and provides explicit capability-typed access.
///
/// This is constructor-injected wherever it is used — it is not a service
/// locator. It resolves over the set of gateways registered in DI and the
/// <c>HealthcareTransactions</c> configuration, so callers depend on the
/// abstraction rather than on any concrete gateway.
/// </summary>
public interface IHealthcareGatewayResolver
{
    /// <summary>
    /// Resolve a gateway by <paramref name="name"/>, or the configured default
    /// gateway when <paramref name="name"/> is null/empty.
    /// </summary>
    /// <exception cref="InvalidOperationException">No matching gateway is registered.</exception>
    IHealthcareTransactionGateway Resolve(string? name = null);

    /// <summary>
    /// Resolve a gateway and return it typed as capability interface
    /// <typeparamref name="TCapability"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No matching gateway is registered.</exception>
    /// <exception cref="GatewayCapabilityNotSupportedException">
    /// The resolved gateway does not support <typeparamref name="TCapability"/>.
    /// </exception>
    TCapability ResolveCapability<TCapability>(string? name = null)
        where TCapability : class, IHealthcareTransactionGateway;
}
