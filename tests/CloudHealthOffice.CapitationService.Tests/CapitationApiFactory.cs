using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using CapitationService.Repositories;
using CapitationService.Services;

namespace CloudHealthOffice.CapitationService.Tests;

/// <summary>
/// WebApplicationFactory for capitation-service smoke tests.
/// Replaces all repository and external-service dependencies with NSubstitute mocks
/// so tests run without Cosmos/Mongo/Stripe/external services.
/// </summary>
public class CapitationApiFactory : WebApplicationFactory<Program>
{
    public ICapitationContractRepository ContractRepository { get; } = Substitute.For<ICapitationContractRepository>();
    public ICapitationRunRepository RunRepository { get; } = Substitute.For<ICapitationRunRepository>();
    public ICapitationStatementRepository StatementRepository { get; } = Substitute.For<ICapitationStatementRepository>();
    public ICapitationDisbursementRepository DisbursementRepository { get; } = Substitute.For<ICapitationDisbursementRepository>();
    public ICapitationRunService RunService { get; } = Substitute.For<ICapitationRunService>();
    public ICapitationDisbursementService DisbursementService { get; } = Substitute.For<ICapitationDisbursementService>();
    public ICapitationEraService EraService { get; } = Substitute.For<ICapitationEraService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Remove real repository, service, and infrastructure registrations
            var typesToRemove = new[]
            {
                typeof(ICapitationContractRepository),
                typeof(ICapitationRunRepository),
                typeof(ICapitationStatementRepository),
                typeof(ICapitationDisbursementRepository),
                typeof(ICapitationRunService),
                typeof(ICapitationDisbursementService),
                typeof(ICapitationEraService),
                typeof(INachaCreditFileService),
                typeof(IStripeConnectService),
                typeof(IStripeTransferClient),
            };

            var descriptorsToRemove = services
                .Where(d => typesToRemove.Contains(d.ServiceType))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
                services.Remove(descriptor);

            // Remove Cosmos/Mongo registrations that would fail without config
            var infraDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Cosmos") == true
                         || d.ServiceType.FullName?.Contains("Mongo") == true)
                .ToList();

            foreach (var descriptor in infraDescriptors)
                services.Remove(descriptor);

            // Register mocks
            services.AddSingleton(ContractRepository);
            services.AddSingleton(RunRepository);
            services.AddSingleton(StatementRepository);
            services.AddSingleton(DisbursementRepository);
            services.AddSingleton(RunService);
            services.AddSingleton(DisbursementService);
            services.AddSingleton(EraService);
            services.AddSingleton(Substitute.For<INachaCreditFileService>());
            services.AddSingleton(Substitute.For<IStripeConnectService>());
            services.AddSingleton(Substitute.For<IStripeTransferClient>());
        });
    }
}
