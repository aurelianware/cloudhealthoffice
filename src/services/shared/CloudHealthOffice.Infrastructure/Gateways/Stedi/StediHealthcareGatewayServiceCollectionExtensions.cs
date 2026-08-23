using CloudHealthOffice.Infrastructure.Gateways;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

/// <summary>
/// Registers the Stedi healthcare transaction gateway and its dependencies,
/// following the same conventions as <c>AddChoHealthcareGateways</c>: options
/// bound from configuration, a named <see cref="System.Net.Http.HttpClient"/>,
/// and the gateway added to the resolvable
/// <see cref="IHealthcareTransactionGateway"/> set.
/// </summary>
public static class StediHealthcareGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Register the Stedi eligibility gateway. Idempotent. Callers normally reach
    /// this indirectly via <c>AddChoHealthcareGateways</c>, which invokes it when
    /// Stedi is configured or selected as the default gateway.
    /// </summary>
    public static IServiceCollection AddStediHealthcareGateway(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<StediGatewayOptions>()
            .Bind(configuration.GetSection(StediGatewayOptions.SectionPath));

        // Named HttpClient: base URL, timeout, and user agent. Authentication is
        // applied per-request inside the client so the API key is never stored
        // on the shared handler. Auth failures are surfaced as gateway errors,
        // never retried.
        services.AddHttpClient(StediEligibilityApiClient.HttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<StediGatewayOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(opts.BaseUrl) &&
                Uri.TryCreate(opts.BaseUrl, UriKind.Absolute, out var baseUri))
            {
                client.BaseAddress = baseUri;
            }
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds > 0 ? opts.TimeoutSeconds : 30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CloudHealthOffice-HealthcareGateway/1.0");
        });

        services.TryAddSingleton<IStediPayerResolver, StediPayerResolver>();

        services.TryAddSingleton(sp => new StediEligibilityApiClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<StediGatewayOptions>>(),
            sp.GetRequiredService<ILogger<StediEligibilityApiClient>>(),
            sp.GetService<TimeProvider>()));

        services.TryAddSingleton(sp => new StediClaimApiClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<StediGatewayOptions>>(),
            sp.GetRequiredService<ILogger<StediClaimApiClient>>(),
            sp.GetService<TimeProvider>()));

        // Build the gateway once (its constructor is internal — a transport
        // detail — and takes an optional TimeProvider), then expose it as an
        // IHealthcareTransactionGateway so the resolver can select it by name.
        // The two-type enumerable overload records the implementation type so
        // TryAddEnumerable stays idempotent.
        services.TryAddSingleton(sp => new StediHealthcareGateway(
            sp.GetRequiredService<StediEligibilityApiClient>(),
            sp.GetRequiredService<IStediPayerResolver>(),
            sp.GetRequiredService<IOptions<StediGatewayOptions>>(),
            sp.GetRequiredService<ILogger<StediHealthcareGateway>>(),
            sp.GetService<TimeProvider>(),
            sp.GetRequiredService<StediClaimApiClient>(),
            sp.GetRequiredService<IClaimTransmissionStore>()));

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHealthcareTransactionGateway, StediHealthcareGateway>(
                sp => sp.GetRequiredService<StediHealthcareGateway>()));

        return services;
    }
}
