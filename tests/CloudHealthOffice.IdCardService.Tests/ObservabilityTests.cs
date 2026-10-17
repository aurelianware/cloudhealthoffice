using CloudHealthOffice.Infrastructure.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CloudHealthOffice.IdCardService.Tests;

public class ObservabilityTests : IClassFixture<ObservabilityTests.Factory>
{
    /// <summary>
    /// The shared observability host, plus the storage idcard-service needs
    /// to start: Program.cs requires MongoDB in every environment.
    /// </summary>
    public sealed class Factory : ObservabilityTestFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseInMemoryStorage();
        }
    }

    private readonly Factory _factory;

    public ObservabilityTests(Factory factory) => _factory = factory;

    [Fact]
    public Task ObservabilityWiring_SatisfiesStandardContract() =>
        ObservabilityTestHelper.AssertStandardContract(_factory);
}
