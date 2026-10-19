using EligibilityService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// <see cref="IEligibilityService"/> is the typed HttpClient registration
/// (5 s timeout, the shared client resilience). A later plain AddScoped
/// registration used to replace it, so the service ran on an unconfigured client.
/// </summary>
public class EligibilityClientRegistrationTests
{
    [Fact]
    public void IEligibilityService_IsRegisteredOnce_AsTheTypedClient()
    {
        List<ServiceDescriptor>? registrations = null;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureServices(services =>
                registrations = services.Where(d => d.ServiceType == typeof(IEligibilityService)).ToList());
        });

        try { _ = factory.Services; } catch { /* only the registrations are inspected, not the running host */ }

        var only = Assert.Single(registrations!);
        Assert.Null(only.ImplementationType); // the typed-client factory, not a bare type mapping
        Assert.NotNull(only.ImplementationFactory);
    }
}
