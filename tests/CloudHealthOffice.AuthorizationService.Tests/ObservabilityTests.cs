using CloudHealthOffice.Infrastructure.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CloudHealthOffice.AuthorizationService.Tests;

public class ObservabilityTests : IClassFixture<ObservabilityTests.Factory>
{
    /// <summary>
    /// The shared observability host, plus the connection string
    /// authorization-service needs to start: Program.cs requires MongoDB in
    /// every environment. The driver connects lazily and the smoke test only
    /// reads /metrics, so nothing reaches the (absent) server.
    /// </summary>
    public sealed class Factory : ObservabilityTestFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("MongoDb:ConnectionString", AuthorizationApiFactory.UnreachableMongo);
        }
    }

    private readonly Factory _factory;

    public ObservabilityTests(Factory factory) => _factory = factory;

    [Fact]
    public Task ObservabilityWiring_SatisfiesStandardContract() =>
        ObservabilityTestHelper.AssertStandardContract(_factory);
}
