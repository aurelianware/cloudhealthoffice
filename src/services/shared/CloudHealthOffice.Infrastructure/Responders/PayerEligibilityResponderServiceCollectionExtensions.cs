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
        services.TryAddSingleton<IPayerEligibilityDirectory, InMemoryPayerEligibilityDirectory>();
        services.TryAddSingleton<IPayerEligibilityRouter, PayerEligibilityRouter>();
        services.TryAddSingleton<IEligibilityResponder, CloudHealthOfficeEligibilityResponder>();
        services.TryAddSingleton<ICanonicalInboundEligibilityAdapter, CanonicalInboundEligibilityAdapter>();
        services.TryAddSingleton<IInboundEligibilityAdapter>(sp =>
            (IInboundEligibilityAdapter)sp.GetRequiredService<ICanonicalInboundEligibilityAdapter>());
        services.TryAddSingleton<StediInboundEligibilityAdapter>();

        return services;
    }
}
