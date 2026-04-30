using ClaimsService.EDI.Florida;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests;

public class ClaimsApiFactory : WebApplicationFactory<Program>
{
    public IClaimRepository ClaimRepository { get; } = Substitute.For<IClaimRepository>();
    public IClaimAcknowledgmentService AcknowledgmentService { get; } = Substitute.For<IClaimAcknowledgmentService>();
    public IAiExaminationAuditRepository AuditRepository { get; } = Substitute.For<IAiExaminationAuditRepository>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Remove real repository and acknowledgment service registrations
            var descriptorsToRemove = services
                .Where(d => d.ServiceType == typeof(IClaimRepository)
                         || d.ServiceType == typeof(IClaimAcknowledgmentService)
                         || d.ServiceType == typeof(IProviderService)
                         || d.ServiceType == typeof(ITenantComplianceConfigService)
                         || d.ServiceType == typeof(IAiExaminationAuditRepository))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }

            // Also remove Cosmos/Mongo registrations that would fail without
            // config. The filter must catch BOTH the service type (e.g.
            // IMongoDatabase) AND the implementation type (e.g.
            // MongoClaimVersionEventPublisher / ClaimVersionEventIndexInitializer
            // which take IMongoDatabase by constructor) — otherwise the DI
            // graph still tries to activate the impl and fails validation.
            var cosmosDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Cosmos") == true
                         || d.ServiceType.FullName?.Contains("Mongo") == true
                         || d.ImplementationType?.FullName?.Contains("Cosmos") == true
                         || d.ImplementationType?.FullName?.Contains("Mongo") == true
                         || d.ImplementationType?.FullName?.Contains("ClaimVersionEventIndexInitializer") == true)
                .ToList();

            foreach (var descriptor in cosmosDescriptors)
            {
                services.Remove(descriptor);
            }

            // Re-register a Noop publisher so any controller path that
            // injects IClaimVersionEventPublisher still resolves cleanly.
            services.AddScoped<IClaimVersionEventPublisher, NoopClaimVersionEventPublisher>();

            services.AddSingleton(ClaimRepository);
            services.AddSingleton(AcknowledgmentService);
            services.AddSingleton(Substitute.For<IProviderService>());
            services.AddSingleton(Substitute.For<ITenantComplianceConfigService>());
            services.AddSingleton(AuditRepository);
        });
    }
}
