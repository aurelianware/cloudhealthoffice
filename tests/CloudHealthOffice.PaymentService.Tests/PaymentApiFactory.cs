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
    public IEraGeneratorService EraGeneratorService { get; } = Substitute.For<IEraGeneratorService>();
    public IPaymentRunService PaymentRunService { get; } = Substitute.For<IPaymentRunService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Remove real repository, service, and infrastructure registrations
            var descriptorsToRemove = services
                .Where(d => d.ServiceType == typeof(IPaymentRepository)
                         || d.ServiceType == typeof(IPaymentRunRepository)
                         || d.ServiceType == typeof(IEraGeneratorService)
                         || d.ServiceType == typeof(IPaymentRunService))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }

            // Remove Cosmos/Mongo registrations that would fail without config
            var cosmosDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Cosmos") == true
                         || d.ServiceType.FullName?.Contains("Mongo") == true)
                .ToList();

            foreach (var descriptor in cosmosDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(PaymentRepository);
            services.AddSingleton(PaymentRunRepository);
            services.AddSingleton(EraGeneratorService);
            services.AddSingleton(PaymentRunService);
        });
    }
}
