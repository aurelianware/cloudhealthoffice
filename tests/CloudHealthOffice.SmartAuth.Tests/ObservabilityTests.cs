using CloudHealthOffice.Infrastructure.Tests;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CloudHealthOffice.SmartAuth.Tests;

// smart-auth-service exposes its Program under the SmartAuthService namespace
// (SmartAuthProgram.cs) to disambiguate from the fhir-service Program in the
// global namespace — this test project references both services.
public class ObservabilityTests : IClassFixture<ObservabilityTestFactory<SmartAuthService.Program>>
{
    private readonly ObservabilityTestFactory<SmartAuthService.Program> _factory;

    public ObservabilityTests(ObservabilityTestFactory<SmartAuthService.Program> factory) =>
        _factory = factory;

    [Fact]
    public Task ObservabilityWiring_SatisfiesStandardContract() =>
        ObservabilityTestHelper.AssertStandardContract(_factory);
}
