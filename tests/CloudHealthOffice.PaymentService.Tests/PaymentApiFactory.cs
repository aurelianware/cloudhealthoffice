using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PaymentService.Repositories;
using PaymentService.Services;

namespace CloudHealthOffice.PaymentService.Tests;

public class PaymentApiFactory : WebApplicationFactory<Program>
{
    public IPaymentRepository PaymentRepository { get; } = Substitute.For<IPaymentRepository>();
    public IPaymentRunRepository PaymentRunRepository { get; } = Substitute.For<IPaymentRunRepository>();
    public IReversalRunRepository ReversalRunRepository { get; } = Substitute.For<IReversalRunRepository>();
    public IEraGeneratorService EraGeneratorService { get; } = Substitute.For<IEraGeneratorService>();
    public IPaymentRunService PaymentRunService { get; } = Substitute.For<IPaymentRunService>();
    public IReversalRunService ReversalRunService { get; } = Substitute.For<IReversalRunService>();
    public IBatchEraGeneratorService BatchEraGeneratorService { get; } = Substitute.For<IBatchEraGeneratorService>();
    public ICarcRarcMappingService CarcRarcMappingService { get; } = Substitute.For<ICarcRarcMappingService>();
    public IEraEnvelopeRepository EraEnvelopeRepository { get; } = Substitute.For<IEraEnvelopeRepository>();
    public ITradingPartnersClient TradingPartnersClient { get; } = Substitute.For<ITradingPartnersClient>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Remove real repository, service, and infrastructure registrations
            var descriptorsToRemove = services
                .Where(d => d.ServiceType == typeof(IPaymentRepository)
                         || d.ServiceType == typeof(IPaymentRunRepository)
                         || d.ServiceType == typeof(IReversalRunRepository)
                         || d.ServiceType == typeof(IEraGeneratorService)
                         || d.ServiceType == typeof(IPaymentRunService)
                         || d.ServiceType == typeof(IReversalRunService)
                         || d.ServiceType == typeof(IBatchEraGeneratorService)
                         || d.ServiceType == typeof(ICarcRarcMappingService)
                         || d.ServiceType == typeof(IEraEnvelopeRepository)
                         || d.ServiceType == typeof(ITradingPartnersClient))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }

            // Remove Cosmos/Mongo registrations that would fail without config.
            // Filter both service and implementation type — EraEnvelopeRepositoryMongo
            // would otherwise leak through as IEraEnvelopeRepository.
            var cosmosDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Cosmos") == true
                         || d.ServiceType.FullName?.Contains("Mongo") == true
                         || d.ImplementationType?.FullName?.Contains("Cosmos") == true
                         || d.ImplementationType?.FullName?.Contains("Mongo") == true)
                .ToList();

            foreach (var descriptor in cosmosDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(PaymentRepository);
            services.AddSingleton(PaymentRunRepository);
            services.AddSingleton(ReversalRunRepository);
            services.AddSingleton(EraGeneratorService);
            services.AddSingleton(PaymentRunService);
            services.AddSingleton(ReversalRunService);
            services.AddSingleton(BatchEraGeneratorService);
            services.AddSingleton(CarcRarcMappingService);
            services.AddSingleton(EraEnvelopeRepository);
            services.AddSingleton(TradingPartnersClient);
        });
    }
}
