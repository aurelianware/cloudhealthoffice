using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests;

public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<CloudHealthOffice.ProviderVerificationService.Program>>
{
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ApiIntegrationTests(WebApplicationFactory<CloudHealthOffice.ProviderVerificationService.Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerEndpoint_Returns200()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("CHO Provider Verification API", content);
    }

    [Fact]
    public async Task NppesLookup_InvalidNpi_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/providers/0000000000/nppes");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NppesLookup_TooShortNpi_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/providers/12345/nppes");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task VerifyProvider_InvalidNpi_Returns404()
    {
        // Invalid Luhn check digit — NPPES adapter returns null → orchestrator returns Failed
        var response = await _client.GetAsync("/api/v1/providers/0000000000/verify");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task IntegrityScore_ReturnsExpectedShape()
    {
        // With null adapters, the NPI won't resolve in NPPES (no HTTP call),
        // but the endpoint should still return a structured response
        var response = await _client.GetAsync("/api/v1/providers/0000000000/integrity-score");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("npi", out _));
        Assert.True(root.TryGetProperty("compositeScore", out _));
        Assert.True(root.TryGetProperty("rating", out _));
        Assert.True(root.TryGetProperty("status", out _));
        Assert.True(root.TryGetProperty("flags", out _));
    }

    [Fact]
    public async Task NppesSearch_EndpointIsRoutable()
    {
        // The NPPES adapter makes real HTTP calls, so we just verify
        // the endpoint is routable (not 404) and accepts the expected parameters.
        var response = await _client.GetAsync("/api/v1/providers/search/nppes?lastName=Smith&state=TX&limit=20");

        // Should be 200 (if NPPES is reachable) or 500 (if network is down) — not 404
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BatchVerify_ReturnsExpectedShape()
    {
        var request = new { npis = new[] { "0000000000" }, tier = 0 };
        var response = await _client.PostAsJsonAsync("/api/v1/providers/verify/batch", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("count", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("results", out _));
    }
}
