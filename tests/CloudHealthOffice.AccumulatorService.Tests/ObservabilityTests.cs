using Microsoft.AspNetCore.Hosting;
using CloudHealthOffice.Infrastructure.Tests;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CloudHealthOffice.AccumulatorService.Tests;

public class ObservabilityTests : IClassFixture<ObservabilityTests.DevelopmentObservabilityFactory>
{
    private readonly DevelopmentObservabilityFactory _factory;

    public ObservabilityTests(DevelopmentObservabilityFactory factory) => _factory = factory;

    /// <summary>
    /// The service now requires a ChoAuth issuer configuration at startup; the
    /// development trust lives in appsettings.Development.json, so the smoke
    /// host runs in the Development environment.
    /// </summary>
    public class DevelopmentObservabilityFactory : ObservabilityTestFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            base.ConfigureWebHost(builder);
        }
    }

    [Fact]
    public Task ObservabilityWiring_SatisfiesStandardContract() =>
        ObservabilityTestHelper.AssertStandardContract(_factory);
}
