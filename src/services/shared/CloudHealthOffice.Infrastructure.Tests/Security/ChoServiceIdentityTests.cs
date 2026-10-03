using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.Infrastructure.Tests.Security;

[ApiController]
[Route("api/service-identity")]
public class PipelineServiceIdentityController : ControllerBase
{
    [HttpGet("platform")]
    [RequirePermission("platform:admin")]
    public IActionResult Platform() => Ok();

    [HttpGet("platform-any")]
    [RequirePermission("platform:tenants,platform:admin")]
    public IActionResult PlatformAny() => Ok();

    [HttpGet("tenant")]
    [RequirePermission("claims:adjust")]
    public IActionResult Tenant() => Ok();

    [HttpGet("identity")]
    [RequireServiceClient("token-service")]
    public IActionResult Identity() => Ok();

    [HttpGet("either")]
    [RequireServiceClient("token-service, claims-service")]
    public IActionResult Either() => Ok();
}

/// <summary>
/// Service tokens act within a tenant on the platform pipeline's behalf. They
/// no longer satisfy <c>platform:*</c> permissions, and an endpoint that one
/// specific service must call names that service with
/// <see cref="RequireServiceClientAttribute"/>.
/// </summary>
public sealed class ChoServiceIdentityTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _app = await ChoAuthenticationPipelineTests.StartAsync(null, null, unmappedService: false);
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<HttpStatusCode> Get(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await _client.SendAsync(request)).StatusCode;
    }

    private static string Service(string clientId, string tenant = "tenant-a")
        => ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, tenant);

    private static string Forge(string issuer, Dictionary<string, object> claims)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = ChoDevelopmentAuth.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(ChoDevelopmentAuth.SymmetricKey)),
                SecurityAlgorithms.HmacSha256),
        });

    // ── platform permissions ────────────────────────────────────────────

    [Theory]
    [InlineData("/api/service-identity/platform")]
    [InlineData("/api/service-identity/platform-any")]
    public async Task ServiceToken_DoesNotSatisfyPlatformPermissions(string path)
    {
        (await Get(path, Service("claims-service"))).Should().Be(HttpStatusCode.Forbidden);
        (await Get(path, Service("token-service", "cho-platform"))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceToken_StillSatisfiesTenantPermissions()
    {
        (await Get("/api/service-identity/tenant", Service("claims-service"))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PlatformAdminUser_SatisfiesPlatformPermission_TenantAdminDoesNot()
    {
        (await Get("/api/service-identity/platform", ChoDevelopmentAuth.UserToken("tenant-a", ChoRolePermissions.PlatformAdmin)))
            .Should().Be(HttpStatusCode.OK);
        (await Get("/api/service-identity/platform", ChoDevelopmentAuth.UserToken("tenant-a", ChoRolePermissions.TenantAdmin)))
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("platform:admin", false)]
    [InlineData("PLATFORM:tenants", false)]
    [InlineData("platform:*", false)]
    [InlineData("claims:void", true)]
    [InlineData("settings:manage", true)]
    public void ServicePrincipal_HasPermission(string permission, bool expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "claims-service"),
            new Claim("azp", "claims-service"),
            new Claim(ChoClaimTypes.Role, ChoServiceRole.Name),
            new Claim(ChoPrincipal.ServiceIssuerMarker, "true"),
        }, "test"));

        ChoPrincipal.HasPermission(principal, permission).Should().Be(expected);
    }

    // ── RequireServiceClient ────────────────────────────────────────────

    [Fact]
    public async Task NamedServiceClient_IsAllowed_WhateverItsTenant()
    {
        (await Get("/api/service-identity/identity", Service("token-service", "cho-platform"))).Should().Be(HttpStatusCode.OK);
        (await Get("/api/service-identity/identity", Service("token-service", "tenant-a"))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OtherServiceClient_IsForbidden()
    {
        (await Get("/api/service-identity/identity", Service("claims-service"))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnyOfList_AllowsEachNamedClientOnly()
    {
        (await Get("/api/service-identity/either", Service("token-service"))).Should().Be(HttpStatusCode.OK);
        (await Get("/api/service-identity/either", Service("claims-service"))).Should().Be(HttpStatusCode.OK);
        (await Get("/api/service-identity/either", Service("member-service"))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(ChoRolePermissions.PlatformAdmin)]
    [InlineData(ChoRolePermissions.TenantAdmin)]
    public async Task UserToken_IsForbidden_EvenWithTheClientIdAsSubject(string role)
    {
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("token-service", "tenant-a", [role]);
        (await Get("/api/service-identity/identity", token)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UserIssuer_WritingServiceRoleAndClientId_IsForbidden()
    {
        var forged = Forge(ChoDevelopmentAuth.UserIssuer, new Dictionary<string, object>
        {
            ["sub"] = "token-service", ["azp"] = "token-service", ["tenant_id"] = "cho-platform",
            ["roles"] = new[] { ChoServiceRole.Name },
        });
        (await Get("/api/service-identity/identity", forged)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceToken_WhoseAzpDisagreesWithSub_IsForbidden()
    {
        var forged = Forge(ChoDevelopmentAuth.ServiceIssuer, new Dictionary<string, object>
        {
            ["sub"] = "token-service", ["azp"] = "claims-service", ["tenant_id"] = "cho-platform",
            ["roles"] = new[] { ChoServiceRole.Name },
        });
        (await Get("/api/service-identity/identity", forged)).Should().Be(HttpStatusCode.Forbidden);

        var noAzp = Forge(ChoDevelopmentAuth.ServiceIssuer, new Dictionary<string, object>
        {
            ["sub"] = "token-service", ["tenant_id"] = "cho-platform", ["roles"] = new[] { ChoServiceRole.Name },
        });
        (await Get("/api/service-identity/identity", noAzp)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        (await Get("/api/service-identity/identity", null)).Should().Be(HttpStatusCode.Unauthorized);
    }
}
