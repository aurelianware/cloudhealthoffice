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

        services.AddHttpClient(StediClaimAttachmentApiClient.HttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<StediGatewayOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(opts.ClaimsBaseUrl) &&
                Uri.TryCreate(opts.ClaimsBaseUrl, UriKind.Absolute, out var claimsUri))
            {
                client.BaseAddress = claimsUri;
            }
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds > 0 ? opts.TimeoutSeconds : 30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CloudHealthOffice-HealthcareGateway/1.0");
        });

        services.AddHttpClient(StediClaimAttachmentApiClient.UploadHttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<StediGatewayOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds > 0 ? opts.TimeoutSeconds : 120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CloudHealthOffice-HealthcareGateway/1.0");
        });

        services.TryAddSingleton(sp => new StediClaimAttachmentApiClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<StediGatewayOptions>>(),
            sp.GetRequiredService<IClaimAttachmentContentStore>(),
            sp.GetRequiredService<ILogger<StediClaimAttachmentApiClient>>(),
            sp.GetService<TimeProvider>()));

        services.AddHttpClient(StediClaimAcknowledgmentApiClient.CoreHttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<StediGatewayOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(opts.CoreBaseUrl) &&
                Uri.TryCreate(opts.CoreBaseUrl, UriKind.Absolute, out var coreUri))
            {
                client.BaseAddress = coreUri;
            }
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds > 0 ? opts.TimeoutSeconds : 30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CloudHealthOffice-HealthcareGateway/1.0");
        });

        services.TryAddSingleton(sp => new StediClaimAcknowledgmentApiClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<StediGatewayOptions>>(),
            sp.GetRequiredService<ILogger<StediClaimAcknowledgmentApiClient>>(),
            sp.GetService<TimeProvider>()));

        // Build the gateway once (its constructor is internal — a transport
        // detail — and takes an optional TimeProvider), then expose it as an
        // IHealthcareTransactionGateway so the resolver can select it by name.
        // The two-type enumerable overload records the implementation type so
        // TryAddEnumerable stays idempotent.
        services.TryAddSingleton(sp => new StediClaimStatusApiClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<StediGatewayOptions>>(),
            sp.GetRequiredService<ILogger<StediClaimStatusApiClient>>(),
            sp.GetService<TimeProvider>()));

        services.TryAddSingleton(sp => new StediHealthcareGateway(
            sp.GetRequiredService<StediEligibilityApiClient>(),
            sp.GetRequiredService<IStediPayerResolver>(),
            sp.GetRequiredService<IOptions<StediGatewayOptions>>(),
            sp.GetRequiredService<ILogger<StediHealthcareGateway>>(),
            sp.GetService<TimeProvider>(),
            sp.GetRequiredService<StediClaimApiClient>(),
            sp.GetRequiredService<IClaimTransmissionStore>(),
            sp.GetRequiredService<StediClaimAcknowledgmentApiClient>(),
            sp.GetRequiredService<StediClaimAttachmentApiClient>(),
            sp.GetRequiredService<IClaimAttachmentTransmissionStore>(),
            sp.GetRequiredService<IClaimAttachmentContentStore>(),
            sp.GetRequiredService<IOptions<HealthcareTransactionOptions>>(),
            sp.GetService<CloudHealthOffice.Infrastructure.Messaging.IMessageBus>(),
            sp.GetRequiredService<IClaimAcknowledgmentStore>(),
            sp.GetRequiredService<IClaimStatusInquiryStore>(),
            sp.GetRequiredService<StediClaimStatusApiClient>()));

        services.AddHostedService<StediClaimAcknowledgmentPoller>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHealthcareTransactionGateway, StediHealthcareGateway>(
                sp => sp.GetRequiredService<StediHealthcareGateway>()));

        return services;
    }
}
