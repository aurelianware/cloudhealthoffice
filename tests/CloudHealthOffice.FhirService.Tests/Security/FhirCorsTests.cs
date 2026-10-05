using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// fhir-service is browser-facing (SMART apps), so it has CORS, but an
/// allowlist only (ChoCors): an unlisted origin gets no
/// Access-Control-Allow-Origin, and credentials are never allowed.
/// </summary>
public class FhirCorsTests : IClassFixture<FhirTestWebAppFactory>
{
    private readonly FhirTestWebAppFactory _factory;

    public FhirCorsTests(FhirTestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task UnlistedOrigin_GetsNoCorsGrant()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/fhir/r4/metadata");
        request.Headers.Add("Origin", "https://evil.example");

        var response = await _factory.CreateClient().SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Preflight_FromUnlistedOrigin_GetsNoCorsGrant()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/fhir/r4/Patient");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");

        var response = await _factory.CreateClient().SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }
}
