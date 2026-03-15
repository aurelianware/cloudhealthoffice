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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Remove real repository and acknowledgment service registrations
            var descriptorsToRemove = services
                .Where(d => d.ServiceType == typeof(IClaimRepository)
                         || d.ServiceType == typeof(IClaimAcknowledgmentService))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }

            // Also remove Cosmos/Mongo registrations that would fail without config
            var cosmosDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Cosmos") == true
                         || d.ServiceType.FullName?.Contains("Mongo") == true)
                .ToList();

            foreach (var descriptor in cosmosDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(ClaimRepository);
            services.AddSingleton(AcknowledgmentService);
        });
    }
}
