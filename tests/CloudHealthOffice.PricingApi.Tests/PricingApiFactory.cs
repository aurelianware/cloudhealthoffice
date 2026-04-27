using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CloudHealthOffice.PricingApi.Tests;

/// <summary>
/// WebApplicationFactory for PricingApi smoke tests.
/// Replaces MongoDB and seed-loader dependencies with Moq stubs so tests
/// run without a real MongoDB instance.
/// </summary>
public class PricingApiFactory : WebApplicationFactory<Program>
{
    public Mock<IFeeScheduleRepository> FeeScheduleRepository { get; } = new();
    public Mock<IApiKeyRepository> ApiKeyRepository { get; } = new();
    public Mock<IUsageRepository> UsageRepository { get; } = new();
    public Mock<IFeeScheduleLoaderService> FeeScheduleLoaderService { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Remove real repository and loader registrations
            var typesToRemove = new[]
            {
                typeof(IFeeScheduleRepository),
                typeof(IApiKeyRepository),
                typeof(IUsageRepository),
                typeof(IFeeScheduleLoaderService),
            };

            foreach (var t in typesToRemove)
            {
                var descriptors = services.Where(d => d.ServiceType == t).ToList();
                foreach (var d in descriptors)
                    services.Remove(d);
            }

            // Remove MongoDB client registrations that would try to connect
            var mongoDeps = services
                .Where(d => d.ServiceType.FullName?.Contains("Mongo") == true)
                .ToList();
            foreach (var d in mongoDeps)
                services.Remove(d);

            // Stub AnySchedulesExistAsync so the startup seeding block exits cleanly
            FeeScheduleLoaderService.Setup(s => s.AnySchedulesExistAsync())
                .ReturnsAsync(true);

            services.AddSingleton(FeeScheduleRepository.Object);
            services.AddSingleton(ApiKeyRepository.Object);
            services.AddSingleton(UsageRepository.Object);
            services.AddSingleton(FeeScheduleLoaderService.Object);
        });
    }
}
