using CloudHealthOffice.Infrastructure.Responders.Adapters;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CloudHealthOffice.Infrastructure.Responders;

/// <summary>
/// Registers the vendor-neutral payer-side eligibility responder, in-memory
/// CHO directory (Development / tests), canonical inbound adapter, and the
/// planned Stedi inbound adapter seam.
/// </summary>
public static class PayerEligibilityResponderServiceCollectionExtensions
{
    public static IServiceCollection AddChoPayerEligibilityResponder(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PayerEligibilityResponderOptions>()
            .Bind(configuration.GetSection(PayerEligibilityResponderOptions.SectionName));

        services.TryAddSingleton<TimeProvider>(_ => TimeProvider.System);

        var options = new PayerEligibilityResponderOptions();
        configuration.GetSection(PayerEligibilityResponderOptions.SectionName).Bind(options);
        if (options.UseInMemoryDirectory)
        {
            services.TryAddSingleton<IPayerEligibilityDirectory, InMemoryPayerEligibilityDirectory>();
        }
        else
        {
            // Keeps DI constructable when no production directory is registered
            // yet, without answering inquiries from the synthetic demo seed.
            services.TryAddSingleton<IPayerEligibilityDirectory, UnconfiguredPayerEligibilityDirectory>();
        }
        services.TryAddSingleton<IPayerEligibilityRouter, PayerEligibilityRouter>();
        services.TryAddSingleton<IEligibilityResponder, CloudHealthOfficeEligibilityResponder>();
        services.TryAddSingleton<ICanonicalInboundEligibilityAdapter, CanonicalInboundEligibilityAdapter>();
        services.TryAddSingleton<IInboundEligibilityAdapter>(sp =>
            (IInboundEligibilityAdapter)sp.GetRequiredService<ICanonicalInboundEligibilityAdapter>());
        services.TryAddSingleton<StediInboundEligibilityAdapter>();

        return services;
    }
}
