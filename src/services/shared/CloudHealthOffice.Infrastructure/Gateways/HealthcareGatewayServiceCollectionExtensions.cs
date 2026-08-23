using CloudHealthOffice.Infrastructure.Gateways.Mock;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Registers the vendor-neutral healthcare transaction gateway abstraction.
///
/// Registration follows the existing Cloud Health Office pattern (see
/// <c>AddChoMessaging</c>): options are bound from configuration, one or more
/// <see cref="IHealthcareTransactionGateway"/> implementations are registered,
/// and an <see cref="IHealthcareGatewayResolver"/> selects among them. Callers
/// depend on the resolver / capability interfaces via constructor injection —
/// there is no service-locator access to concrete gateways.
/// </summary>
public static class HealthcareGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Bind <see cref="HealthcareTransactionOptions"/> from the
    /// <c>HealthcareTransactions</c> section, register the resolver, and add the
    /// mock development gateway. Future vendor gateways (Stedi, Availity, direct
    /// X12) are added with <see cref="AddHealthcareGateway{TGateway}"/> without
    /// changing this method.
    /// </summary>
    public static IServiceCollection AddChoHealthcareGateways(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<HealthcareTransactionOptions>()
            .Bind(configuration.GetSection(HealthcareTransactionOptions.SectionName));

        services.TryAddSingleton<IHealthcareGatewayResolver, HealthcareGatewayResolver>();

        // The mock gateway is always available so the abstraction resolves in
        // development and test even when no vendor is configured. It is the
        // default target of HealthcareTransactions:DefaultGateway.
        services.AddHealthcareGateway<MockHealthcareGateway>();

        // Register the Stedi gateway whenever it is configured or explicitly
        // selected as the default. When Stedi is selected but its configuration
        // is incomplete, the Stedi gateway (not Mock) is resolved and returns a
        // clear Configuration error — there is no silent fallback to Mock.
        var defaultGateway = configuration[$"{HealthcareTransactionOptions.SectionName}:DefaultGateway"];
        var stediConfigured = configuration.GetSection(StediGatewayOptions.SectionPath).Exists();
        var stediIsDefault = string.Equals(defaultGateway, StediHealthcareGateway.GatewayName, StringComparison.OrdinalIgnoreCase);
        if (stediConfigured || stediIsDefault)
        {
            services.AddStediHealthcareGateway(configuration);
        }

        return services;
    }

    /// <summary>
    /// Register an additional <see cref="IHealthcareTransactionGateway"/>
    /// implementation as a singleton. Idempotent per implementation type.
    /// </summary>
    public static IServiceCollection AddHealthcareGateway<TGateway>(this IServiceCollection services)
        where TGateway : class, IHealthcareTransactionGateway
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHealthcareTransactionGateway, TGateway>());
        return services;
    }
}
