using CloudHealthOffice.Infrastructure.Tests;
using CloudHealthOffice.Testing.FhirService;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests;

public class ObservabilityTests : IClassFixture<ObservabilityTests.FhirObservabilityFactory>
{
    /// <summary>
    /// fhir-service requires MongoDB in every environment; this smoke test never
    /// touches it, so it gets an unreachable connection string and in-process stores.
    /// </summary>
    public class FhirObservabilityFactory : ObservabilityTestFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseUnreachableMongoWithInMemoryStores();
            base.ConfigureWebHost(builder);
        }
    }

    private readonly FhirObservabilityFactory _factory;

    public ObservabilityTests(FhirObservabilityFactory factory) => _factory = factory;

    [Fact]
    public Task ObservabilityWiring_SatisfiesStandardContract() =>
        ObservabilityTestHelper.AssertStandardContract(_factory);
}
