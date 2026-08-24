using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Responders.Adapters;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Responders;

public static class ClaimAttachmentReceiverServiceCollectionExtensions
{
    public static IServiceCollection AddChoPayerClaimAttachmentReceiver(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PayerEligibilityResponderOptions>()
            .Bind(configuration.GetSection(PayerEligibilityResponderOptions.SectionName));

        var options = new PayerEligibilityResponderOptions();
        configuration.GetSection(PayerEligibilityResponderOptions.SectionName).Bind(options);
        if (options.UseInMemoryDirectory)
        {
            services.TryAddSingleton<IPayerClaimDirectory, InMemoryPayerClaimDirectory>();
        }
        else
        {
            services.TryAddSingleton<IPayerClaimDirectory, UnconfiguredPayerClaimDirectory>();
        }

        services.TryAddSingleton<IPayerEligibilityRouter, PayerEligibilityRouter>();
        services.TryAddSingleton<IInboundAttachmentScanner>(_ => NullInboundAttachmentScanner.Instance);
        services.TryAddSingleton(sp =>
            new CloudHealthOfficeClaimAttachmentReceiver(
                sp.GetRequiredService<IPayerEligibilityRouter>(),
                sp.GetRequiredService<IPayerClaimDirectory>(),
                sp.GetRequiredService<IClaimAttachmentContentStore>(),
                sp.GetRequiredService<IInboundClaimAttachmentReceiptStore>(),
                sp.GetRequiredService<ILogger<CloudHealthOfficeClaimAttachmentReceiver>>(),
                sp.GetService<IOptions<HealthcareTransactionOptions>>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<IMessageBus>(),
                sp.GetService<IInboundAttachmentScanner>()));
        services.TryAddSingleton<IClaimAttachmentReceiver>(sp =>
            sp.GetRequiredService<CloudHealthOfficeClaimAttachmentReceiver>());
        services.TryAddSingleton<ICanonicalInboundClaimAttachmentAdapter, CanonicalInboundClaimAttachmentAdapter>();
        services.TryAddSingleton<IInboundClaimAttachmentAdapter>(sp =>
            (IInboundClaimAttachmentAdapter)sp.GetRequiredService<ICanonicalInboundClaimAttachmentAdapter>());
        services.TryAddSingleton<StediInboundClaimAttachmentAdapter>();
        services.TryAddSingleton<X12InboundClaimAttachmentAdapter>();
        services.AddHostedService<InboundClaimAttachmentOutboxPublisher>();
        return services;
    }
}
