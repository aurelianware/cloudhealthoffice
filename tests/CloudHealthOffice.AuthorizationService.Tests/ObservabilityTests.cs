using CloudHealthOffice.Infrastructure.Tests;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CloudHealthOffice.AuthorizationService.Tests;

public class ObservabilityTests : IClassFixture<ObservabilityTestFactory<Program>>
{
    private readonly ObservabilityTestFactory<Program> _factory;

    public ObservabilityTests(ObservabilityTestFactory<Program> factory) => _factory = factory;

    [Fact]
    public Task ObservabilityWiring_SatisfiesStandardContract() =>
        ObservabilityTestHelper.AssertStandardContract(_factory);
}
