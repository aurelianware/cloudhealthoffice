using System.Net;
using System.Net.Http.Headers;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.Infrastructure.Tests.Security;

[ApiController]
[Route("api/widgets")]
public class PipelineWidgetsController : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(new { tenant = HttpContext.GetTenantId() });

    [HttpPost]
    public IActionResult Create([FromServices] ICurrentActor actor)
        => Ok(new { tenant = actor.TenantId, actor = actor.UserId });

    [HttpPost("adjust")]
    [RequirePermission("claims:adjust")]
    public IActionResult Adjust() => Ok();

    [HttpGet("public")]
    [AllowAnonymous]
    public IActionResult Public() => Ok();
}

[ApiController]
[Route("api/unmapped")]
public class PipelineUnmappedController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

/// <summary>
/// End-to-end behaviour of AddChoAuthentication/UseChoAuthentication on a real
/// host: 401 without a token, 403 without the permission, the tenant from the
/// token, and spoofed tenant headers rejected (review items P6, P7, §8.10).
/// </summary>
public sealed class ChoAuthenticationPipelineTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _app = await StartAsync(defaultRead: "claims:read", defaultWrite: "claims:work", unmappedService: false);
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    internal static async Task<WebApplication> StartAsync(string? defaultRead, string? defaultWrite, bool unmappedService)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(ChoDevelopmentAuth.Configuration());
        builder.Services.AddControllers().AddApplicationPart(typeof(PipelineWidgetsController).Assembly);
        builder.Services.AddChoHealthChecks(_ => { });
        builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, d =>
        {
            d.DefaultReadPermission = defaultRead;
            d.DefaultWritePermission = defaultWrite;
        });

        var app = builder.Build();
        app.UseChoAuthentication();
        app.MapControllers();
        app.MapChoHealthChecks();
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token, string? tenantHeader = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader != null) request.Headers.Add("X-Tenant-ID", tenantHeader);
        return request;
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/widgets", null));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HeaderOnly_NoToken_Returns401()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/widgets", null, "tenant-a"));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ValidToken_ResolvesTenantFromToken()
    {
        var token = ChoDevelopmentAuth.UserToken("tenant-a", ChoRolePermissions.ClaimsExaminer);
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/widgets", token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("tenant-a");
    }

    [Fact]
    public async Task SpoofedTenantHeader_Returns403()
    {
        var token = ChoDevelopmentAuth.UserToken("tenant-a", ChoRolePermissions.ClaimsExaminer);
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/widgets", token, "tenant-b"));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ActorComesFromToken()
    {
        var token = ChoDevelopmentAuth.UserTokenIssuer()
            .IssueUserToken("user-42", "tenant-a", [ChoRolePermissions.ClaimsExaminer]);
        var request = Request(HttpMethod.Post, "/api/widgets", token);
        request.Headers.Add("X-User-Id", "someone-else");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("user-42").And.NotContain("someone-else");
    }

    [Fact]
    public async Task RoleWithoutDefaultReadPermission_Returns403()
    {
        var token = ChoDevelopmentAuth.UserToken("tenant-a", ChoRolePermissions.ProviderRelations);
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/widgets", token));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ExplicitPermission_DeniedForExaminer_AllowedForSupervisor()
    {
        var examiner = ChoDevelopmentAuth.UserToken("tenant-a", ChoRolePermissions.ClaimsExaminer);
        var supervisor = ChoDevelopmentAuth.UserToken("tenant-a", ChoRolePermissions.ClaimsSupervisor);

        (await _client.SendAsync(Request(HttpMethod.Post, "/api/widgets/adjust", examiner)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Request(HttpMethod.Post, "/api/widgets/adjust", supervisor)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UserIssuerCannotMintServiceRole()
    {
        // The portal's user-token issuer writes the reserved role into the
        // roles claim directly; it must grant nothing.
        var forged = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ChoDevelopmentAuth.UserIssuer,
            Audience = ChoDevelopmentAuth.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "intruder", ["tenant_id"] = "tenant-a", ["roles"] = new[] { ChoServiceRole.Name },
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(ChoDevelopmentAuth.SymmetricKey)),
                SecurityAlgorithms.HmacSha256),
        });

        var response = await _client.SendAsync(Request(HttpMethod.Post, "/api/widgets/adjust", forged));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceToken_FromServiceIssuer_IsAuthorized()
    {
        var token = ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("claims-service", "tenant-a");
        var response = await _client.SendAsync(Request(HttpMethod.Post, "/api/widgets/adjust", token));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WrongAudience_Returns401()
    {
        var token = ChoTokenIssuer.FromKeys(ChoDevelopmentAuth.UserIssuer, "some-other-api", null,
                ChoDevelopmentAuth.SymmetricKey, TimeSpan.FromMinutes(5))
            .IssueUserToken("u", "tenant-a", [ChoRolePermissions.TenantAdmin]);
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/widgets", token));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UntrustedIssuer_Returns401()
    {
        var token = ChoTokenIssuer.FromKeys("someone-else", ChoDevelopmentAuth.Audience, null,
                ChoDevelopmentAuth.SymmetricKey, TimeSpan.FromMinutes(5))
            .IssueUserToken("u", "tenant-a", [ChoRolePermissions.TenantAdmin]);
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/widgets", token));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredToken_Returns401()
    {
        var token = ChoDevelopmentAuth.UserTokenIssuer(TimeSpan.FromMinutes(-10))
            .IssueUserToken("u", "tenant-a", [ChoRolePermissions.TenantAdmin]);
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/widgets", token));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AllowAnonymousEndpoint_IsServedWithoutToken()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/widgets/public", null));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    public async Task HealthProbes_NeedNoToken(string path)
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, path, null));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnknownRoute_WithoutToken_Returns401NotRouteDisclosure()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/nope", null));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ServiceWithoutDefaults_DeniesUnannotatedActions()
    {
        await using var app = await StartAsync(defaultRead: null, defaultWrite: null, unmappedService: true);
        var client = app.GetTestClient();
        var token = ChoDevelopmentAuth.UserToken("tenant-a", ChoRolePermissions.TenantAdmin);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/api/unmapped", token));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void SymmetricKey_RefusedOutsideDevelopmentAndTesting()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(ChoDevelopmentAuth.Configuration());

        var act = () => builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment);

        act.Should().Throw<InvalidOperationException>().WithMessage("*symmetric key*");
    }

    [Fact]
    public void NoIssuers_FailsStartup()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });

        var act = () => builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Issuers is empty*");
    }
}
