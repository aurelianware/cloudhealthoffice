using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Security;
using Xunit;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests;

public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<CloudHealthOffice.ProviderVerificationService.Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<CloudHealthOffice.ProviderVerificationService.Program> _factory;

    public ApiIntegrationTests(WebApplicationFactory<CloudHealthOffice.ProviderVerificationService.Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
        });
        // Every API call carries a development CHO token for this tenant.
        _client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-a");
    }

    // ── Health checks ────────────────────────────────────────────

    [Fact]
    public async Task HealthLive_Returns200()
    {
        var response = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_Returns200()
    {
        var response = await _client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthBackwardCompat_Returns200()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Swagger ──────────────────────────────────────────────────

    [Fact]
    public async Task SwaggerEndpoint_Returns200_InDevelopment()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Cloud Health Office Provider Verification API", content);
    }

    [Fact]
    public async Task SwaggerEndpoint_Returns404_InProduction()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var prodFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            // Production refuses symmetric (development) keys and starts only
            // with a trusted issuer configured: give it an asymmetric one.
            builder.UseSetting("ChoAuth:Issuers:0:Issuer", "cho-token-service");
            builder.UseSetting("ChoAuth:Issuers:0:PublicKeyPem", key.ExportSubjectPublicKeyInfoPem());
        });
        // An authenticated caller, so the 404 shows Swagger is not mapped
        // (an anonymous caller is refused with 401 before routing matters).
        var token = ChoTokenIssuer
            .FromKeys("cho-token-service", ChoDevelopmentAuth.Audience, key.ExportPkcs8PrivateKeyPem(), null, TimeSpan.FromMinutes(5))
            .IssueUserToken("prod-user", "tenant-a", [ChoRolePermissions.TenantAdmin]);
        var prodClient = prodFactory.CreateClient();
        prodClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await prodClient.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── NPPES ────────────────────────────────────────────────────

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
    public async Task NppesSearch_EndpointIsRoutable()
    {
        var response = await _client.GetAsync("/api/v1/providers/search/nppes?lastName=Smith&state=TX&limit=20");
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Verify / Integrity Score ─────────────────────────────────

    [Fact]
    public async Task VerifyProvider_InvalidNpi_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/providers/0000000000/verify");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task IntegrityScore_ReturnsExpectedShape()
    {
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

    // ── Batch verification ───────────────────────────────────────

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

    [Fact]
    public async Task BatchVerify_NullBody_Returns400()
    {
        var content = new StringContent("null", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/v1/providers/verify/batch", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BatchVerify_EmptyNpiList_Returns400()
    {
        var request = new { npis = Array.Empty<string>() };
        var response = await _client.PostAsJsonAsync("/api/v1/providers/verify/batch", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BatchVerify_ExceedsMaxBatchSize_Returns400()
    {
        var npis = Enumerable.Range(0, 101).Select(i => $"{i:D10}").ToArray();
        var request = new { npis };
        var response = await _client.PostAsJsonAsync("/api/v1/providers/verify/batch", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("100", body);
    }

    [Fact]
    public async Task BatchVerify_ContainsWhitespaceNpi_Returns400()
    {
        var request = new { npis = new[] { "1234567893", "  ", "1497758544" } };
        var response = await _client.PostAsJsonAsync("/api/v1/providers/verify/batch", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalidIndices", body);
    }
}
