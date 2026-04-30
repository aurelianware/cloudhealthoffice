using ClaimsService.Adapters;
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
    public IClaimSubmissionService SubmissionService { get; } = Substitute.For<IClaimSubmissionService>();
    public IClaimAdapter ClaimAdapter { get; } = CreateChoStubAdapter();
    public IClaimVersionEventPublisher VersionEventPublisher { get; } = Substitute.For<IClaimVersionEventPublisher>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Remove real repository, services, and adapter registrations.
            var descriptorsToRemove = services
                .Where(d => d.ServiceType == typeof(IClaimRepository)
                         || d.ServiceType == typeof(IClaimAcknowledgmentService)
                         || d.ServiceType == typeof(IProviderService)
                         || d.ServiceType == typeof(ITenantComplianceConfigService)
                         || d.ServiceType == typeof(IAiExaminationAuditRepository)
                         || d.ServiceType == typeof(IClaimSubmissionService)
                         || d.ServiceType == typeof(IClaimAdapter)
                         || d.ServiceType == typeof(ClaimAdapterFactory)
                         || d.ServiceType == typeof(ClaimTenantConfigCache))
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

            // Also remove the existing IClaimVersionEventPublisher registration
            // (Mongo or Noop) — tests inject their own mock so they can
            // assert on emission without touching infrastructure.
            var publisherDescriptors = services
                .Where(d => d.ServiceType == typeof(IClaimVersionEventPublisher))
                .ToList();
            foreach (var descriptor in publisherDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(ClaimRepository);
            services.AddSingleton(AcknowledgmentService);
            services.AddSingleton(Substitute.For<IProviderService>());
            services.AddSingleton(Substitute.For<ITenantComplianceConfigService>());
            services.AddSingleton(AuditRepository);
            services.AddSingleton(VersionEventPublisher);

            // V1 controller depends on ClaimAdapterFactory directly (5.2 +
            // 5.3 wiring). Register a real factory whose only registered
            // adapter is the stub (default "cho" platform), and a real
            // ClaimTenantConfigCache that won't actually call out because
            // we never trigger a cache miss to a live URL.
            services.AddSingleton<IClaimAdapter>(_ => ClaimAdapter);

            // Reuse the configuration / HTTP client factory already registered
            // by AddChoInfrastructure.
            services.AddScoped<ClaimAdapterFactory>();
            services.AddSingleton<ClaimTenantConfigCache>();

            // Submission service is mocked by default so tests can assert on
            // controller-level behavior (validation passthrough,
            // CreatedAtAction, deprecation headers) without standing up the
            // full orchestration. Tests that need the real service
            // (ClaimSubmissionServiceTests) instantiate it directly.
            services.AddSingleton(SubmissionService);
        });
    }

    private static IClaimAdapter CreateChoStubAdapter()
    {
        var adapter = Substitute.For<IClaimAdapter>();
        adapter.Platform.Returns("cho");
        return adapter;
    }
}
