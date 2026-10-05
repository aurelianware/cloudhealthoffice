using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CloudHealthOffice.IdCardService.Tests.Security;

/// <summary>
/// With a real provider IdP configured (as in production), the provider JWT
/// scheme lives beside the CHO scheme: the service starts, CHO endpoints take
/// CHO tokens, and a CHO token is not a provider credential for /scan.
/// </summary>
public class ProviderSchemeTests : IClassFixture<ProviderSchemeTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            foreach (var (key, value) in ChoDevelopmentAuth.Configuration("idcard-service"))
                builder.UseSetting(key, value);
            builder.UseSetting("ProviderJwt:Authority", "https://provider-idp.invalid");
            builder.UseSetting("ProviderJwt:Audience", "idcard-scan");
            builder.UseSetting("Messaging:Backend", "InMemory");
            builder.UseSetting("MongoDb:ConnectionString", "");
            builder.UseSetting("CosmosDb:ConnectionString", "");
        }
    }

    private readonly Factory _factory;

    public ProviderSchemeTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task ChoEndpoints_AcceptChoTokens_WhenProviderSchemeIsConfigured()
    {
        // Before: the provider scheme was registered as the default "Bearer"
        // scheme, which collides with the CHO scheme.
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(ChoRolePermissions.MemberServices));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-1");

        var response = await client.GetAsync("/api/v1/members/mem-1/id-cards");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Scan_ChoTokenIsNotAProviderCredential()
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-1");

        var response = await client.PostAsJsonAsync("/api/v1/id-cards/scan", new { qrPayload = "x.y" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Scan_WithoutCredentials_IsRejected()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/id-cards/scan", new { qrPayload = "x.y" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
