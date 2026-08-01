using ClaimsService.Adapters;
using ClaimsService.EDI.Florida;
using ClaimsService.HostedServices;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.NcciEngine.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests;

public class ClaimsApiFactory : WebApplicationFactory<Program>
{
    public IClaimRepository ClaimRepository { get; } = Substitute.For<IClaimRepository>();
    public IMassAdjudicationRunRepository MassAdjudicationRunRepository { get; } = Substitute.For<IMassAdjudicationRunRepository>();
    public IClaimAcknowledgmentService AcknowledgmentService { get; } = Substitute.For<IClaimAcknowledgmentService>();
    public IAiExaminationAuditRepository AuditRepository { get; } = Substitute.For<IAiExaminationAuditRepository>();
    public IClaimSubmissionService SubmissionService { get; } = Substitute.For<IClaimSubmissionService>();
    public IClaimAdapter ClaimAdapter { get; } = CreateChoStubAdapter();
    public IClaimVersionEventPublisher VersionEventPublisher { get; } = Substitute.For<IClaimVersionEventPublisher>();
    public IClaimVersionEventReader VersionEventReader { get; } = Substitute.For<IClaimVersionEventReader>();
    public IClaimFinalizationService FinalizationService { get; } = Substitute.For<IClaimFinalizationService>();
    public IClaimAdjustmentRepository AdjustmentRepository { get; } = Substitute.For<IClaimAdjustmentRepository>();
    public IClaimAdjustmentService AdjustmentService { get; } = Substitute.For<IClaimAdjustmentService>();
    public IClaimImportTransactionRepository ImportTransactionRepository { get; } = Substitute.For<IClaimImportTransactionRepository>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // 5.5 — force the no-op messaging backend so the orchestrator's
        // SubscriptionHostedService starts a subscription that never
        // dispatches, and ClaimSubmissionService's SendAsync silently
        // succeeds. Tests that need actual adjudication-pipeline
        // behaviour use a dedicated integration factory with
        // Messaging:Backend=InMemory.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Messaging:Backend"] = "Null",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove real repository, services, and adapter registrations.
            var descriptorsToRemove = services
                .Where(d => d.ServiceType == typeof(IClaimRepository)
                         || d.ServiceType == typeof(IMassAdjudicationRunRepository)
                         || d.ServiceType == typeof(IClaimAcknowledgmentService)
                         || d.ServiceType == typeof(IProviderService)
                         || d.ServiceType == typeof(ITenantComplianceConfigService)
                         || d.ServiceType == typeof(IAiExaminationAuditRepository)
                         || d.ServiceType == typeof(IClaimSubmissionService)
                         || d.ServiceType == typeof(IClaimFinalizationService)
                         || d.ServiceType == typeof(IClaimAdjustmentService)
                         || d.ServiceType == typeof(IClaimAdjustmentRepository)
                         || d.ServiceType == typeof(IClaimImportTransactionRepository)
                         || d.ServiceType == typeof(IClaimVersionEventReader)
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
                         || d.ImplementationType == typeof(ClaimIndexInitializer)
                         || d.ImplementationType == typeof(MassAdjudicationRunIndexInitializer)
                         || d.ImplementationType == typeof(ClaimVersionEventIndexInitializer)
                         || d.ImplementationType == typeof(ClaimAdjustmentIndexInitializer))
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

            // 5.5 — replace the production IBenefitCalculationEngine HTTP
            // shim so unrelated controller tests don't trigger a real HTTP
            // call to benefit-plan-service. The SubscriptionHostedService
            // stays registered but consumes a NullMessageBus subscription
            // (configured above) so it's a true no-op for unrelated tests.
            var enginePos = services
                .Where(d => d.ServiceType == typeof(IBenefitCalculationEngine))
                .ToList();
            foreach (var descriptor in enginePos)
            {
                services.Remove(descriptor);
            }
            services.AddSingleton(Substitute.For<IBenefitCalculationEngine>());

            services.AddSingleton(ClaimRepository);
            services.AddSingleton(MassAdjudicationRunRepository);
            services.AddSingleton(AcknowledgmentService);
            services.AddSingleton(Substitute.For<IProviderService>());
            services.AddSingleton(Substitute.For<ITenantComplianceConfigService>());
            services.AddSingleton(AuditRepository);
            services.AddSingleton(VersionEventPublisher);
            services.AddSingleton(VersionEventReader);
            VersionEventReader
                .GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ClaimVersionEvent>());
            services.AddSingleton(FinalizationService);
            services.AddSingleton(AdjustmentRepository);
            services.AddSingleton(AdjustmentService);
            services.AddSingleton(ImportTransactionRepository);

            // 5.7 — NcciEngine's repository implementation got removed by
            // the Cosmos/Mongo filter above. The engine's INcciEditService
            // still depends on INcciRepository — register a substitute so
            // ServiceProvider validation succeeds. Controller-level tests
            // don't drive the adjudication pipeline, so the substitute
            // never gets called; integration tests that need real NCCI
            // behavior wire their own repository directly (see
            // AdjudicationWithNcciEndToEndTests).
            services.AddSingleton(Substitute.For<INcciRepository>());

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
