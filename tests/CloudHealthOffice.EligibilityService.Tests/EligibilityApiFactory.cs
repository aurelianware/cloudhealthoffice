using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using EligibilityService.Adapters;
using EligibilityService.Repositories;
using EligibilityService.Services;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// WebApplicationFactory for eligibility-service smoke tests.
/// Replaces repository and service dependencies with NSubstitute mocks
/// so tests run without MongoDB/Cosmos or external eligibility adapters.
/// </summary>
public class EligibilityApiFactory : WebApplicationFactory<Program>
{
    public IEligibilityRepository EligibilityRepository { get; } = Substitute.For<IEligibilityRepository>();
    public IEligibilityService EligibilityService { get; } = Substitute.For<IEligibilityService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            var typesToRemove = new[]
            {
                typeof(IEligibilityRepository),
                typeof(IEligibilityService),
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

            // Remove eligibility adapter registrations.
            // Narrow to IEligibilityAdapter specifically — a broader substring
            // match on "EligibilityAdapter" would also strip
            // EligibilityAdapterFactory, which BatchEligibilityService needs
            // to construct. The factory itself is harmless in tests because
            // nothing in these tests exercises it.
            var adapterDescriptors = services
                .Where(d => d.ServiceType == typeof(IEligibilityAdapter))
                .ToList();

            foreach (var descriptor in adapterDescriptors)
                services.Remove(descriptor);

            services.AddSingleton(EligibilityRepository);
            services.AddSingleton(EligibilityService);
        });
    }
}
