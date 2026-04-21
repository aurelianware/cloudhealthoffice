using CloudHealthOffice.Infrastructure.Tests;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CloudHealthOffice.AuthorizationService.Tests;

public class ObservabilityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ObservabilityTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public Task ObservabilityWiring_SatisfiesStandardContract() =>
        ObservabilityTestHelper.AssertStandardContract(_factory);
}
