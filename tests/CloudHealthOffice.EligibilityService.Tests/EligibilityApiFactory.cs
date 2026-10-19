using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using EligibilityService.Adapters;
using EligibilityService.Repositories;
using EligibilityService.Services;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// WebApplicationFactory for eligibility-service smoke tests.
/// Replaces repository and service dependencies with NSubstitute mocks
/// so tests run without MongoDB or external eligibility adapters.
/// </summary>
public class EligibilityApiFactory : WebApplicationFactory<Program>
{
    public IEligibilityRepository EligibilityRepository { get; } = Substitute.For<IEligibilityRepository>();
    public IEligibilityService EligibilityService { get; } = Substitute.For<IEligibilityService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Program.cs requires a Mongo connection string and reads it before
        // ConfigureAppConfiguration applies, so it goes in through UseSetting.
        // The Mongo registrations are removed below; nothing connects here.
        builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-eligibility-host:27017");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HealthcareTransactions:Gateways:Stedi:WebhookCredentialValue"] = "test-webhook-secret",
                ["HealthcareTransactions:Gateways:Stedi:WebhookCredentialHeaderName"] = "Authorization",
                ["HealthcareTransactions:ClaimAttachments:MaxContentLengthBytes"] = "32"
            });
        });

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

            // Remove the Mongo registrations that would reach for the fake host
            var infraDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Mongo") == true)
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

            // Drop the MongoDB health check: /health would probe the fake host.
            services.PostConfigure<HealthCheckServiceOptions>(o =>
            {
                foreach (var check in o.Registrations.Where(r => r.Name == "mongodb").ToList())
                    o.Registrations.Remove(check);
            });

            services.AddSingleton(EligibilityRepository);
            services.AddSingleton(EligibilityService);
        });
    }
}
