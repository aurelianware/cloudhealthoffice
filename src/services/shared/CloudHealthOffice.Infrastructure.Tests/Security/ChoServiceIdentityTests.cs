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

    [HttpGet("workload")]
    [RequireServiceClient("wf-tenant-onboarding")]
    public IActionResult Workload() => Ok();

    [HttpGet("enrollment")]
    [RequirePermission("enrollment:process")]
    public IActionResult Enrollment() => Ok();
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

    // ── issuer markers come from configuration only ─────────────────────

    [Fact]
    public async Task UserIssuer_WritingTheServiceIssuerMarkerItself_IsForbidden()
    {
        var forged = Forge(ChoDevelopmentAuth.UserIssuer, new Dictionary<string, object>
        {
            ["sub"] = "token-service", ["azp"] = "token-service", ["tenant_id"] = "cho-platform",
            ["roles"] = new[] { ChoServiceRole.Name },
            [ChoPrincipal.ServiceIssuerMarker] = "true",
        });
        (await Get("/api/service-identity/identity", forged)).Should().Be(HttpStatusCode.Forbidden);
        (await Get("/api/service-identity/tenant", forged)).Should().Be(HttpStatusCode.Forbidden);
    }

    // ── workload identities ─────────────────────────────────────────────

    private static string Workload(string clientId, string tenant = "tenant-a", params string[] permissions)
        => ChoDevelopmentAuth.WorkloadTokenIssuer()
            .IssueWorkloadToken(clientId, tenant, permissions.Length > 0 ? permissions : ["enrollment:process"]);

    [Fact]
    public async Task WorkloadToken_HoldsExactlyItsPermissions()
    {
        var token = Workload("wf-enrollment-import");
        (await Get("/api/service-identity/enrollment", token)).Should().Be(HttpStatusCode.OK);
        (await Get("/api/service-identity/tenant", token)).Should().Be(HttpStatusCode.Forbidden);
        (await Get("/api/service-identity/platform", token)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WorkloadToken_NeverSatisfiesAPlatformPermission_EvenIfListed()
    {
        var forged = Forge(ChoDevelopmentAuth.WorkloadIssuer, new Dictionary<string, object>
        {
            ["sub"] = "wf-x", ["azp"] = "wf-x", ["tenant_id"] = "tenant-a",
            ["roles"] = new[] { ChoWorkloadRole.Name },
            ["permissions"] = new[] { "platform:admin", "platform:tenants", "*:*" },
        });
        (await Get("/api/service-identity/platform", forged)).Should().Be(HttpStatusCode.Forbidden);
        (await Get("/api/service-identity/platform-any", forged)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WorkloadToken_WithNoPermissions_GetsNothingFromItsRole()
    {
        var forged = Forge(ChoDevelopmentAuth.WorkloadIssuer, new Dictionary<string, object>
        {
            ["sub"] = "wf-x", ["azp"] = "wf-x", ["tenant_id"] = "tenant-a",
            ["roles"] = new[] { ChoWorkloadRole.Name, ChoRolePermissions.TenantAdmin },
        });
        (await Get("/api/service-identity/tenant", forged)).Should().Be(HttpStatusCode.Forbidden);
        (await Get("/api/service-identity/enrollment", forged)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NamedWorkloadClient_IsAllowed_OtherWorkloadsAreNot()
    {
        (await Get("/api/service-identity/workload", Workload("wf-tenant-onboarding"))).Should().Be(HttpStatusCode.OK);
        (await Get("/api/service-identity/workload", Workload("wf-enrollment-import"))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceToken_NamingAWorkloadClient_IsForbidden()
    {
        (await Get("/api/service-identity/workload", Service("wf-tenant-onboarding"))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WorkloadToken_NamingAServiceClient_IsForbidden()
    {
        var forged = Forge(ChoDevelopmentAuth.WorkloadIssuer, new Dictionary<string, object>
        {
            ["sub"] = "token-service", ["azp"] = "token-service", ["tenant_id"] = "cho-platform",
            ["roles"] = new[] { ChoWorkloadRole.Name, ChoServiceRole.Name },
            ["permissions"] = new[] { "claims:adjust" },
        });
        (await Get("/api/service-identity/identity", forged)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(ChoDevelopmentAuth.UserIssuer)]
    [InlineData(ChoDevelopmentAuth.ServiceIssuer)]
    public async Task OtherIssuers_CannotMintAWorkloadIdentity(string issuer)
    {
        var forged = Forge(issuer, new Dictionary<string, object>
        {
            ["sub"] = "wf-tenant-onboarding", ["azp"] = "wf-tenant-onboarding", ["tenant_id"] = "tenant-a",
            ["roles"] = new[] { ChoWorkloadRole.Name },
            ["permissions"] = new[] { "enrollment:process" },
            [ChoPrincipal.WorkloadIssuerMarker] = "true",
        });
        // The user issuer's token is accepted but is no workload (403); the
        // service issuer's is refused outright for lacking cho.service (401).
        (await Get("/api/service-identity/workload", forged)).Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    // ── one issuer, one kind of actor ───────────────────────────────────

    [Theory]
    [InlineData(ChoDevelopmentAuth.ServiceIssuer)]
    [InlineData(ChoDevelopmentAuth.WorkloadIssuer)]
    public async Task ServiceOrWorkloadIssuer_CannotMintAUserToken(string issuer)
    {
        // Every service holds cho-internal's key. A token from it without
        // cho.service must not be read as a PlatformAdmin user.
        var forged = Forge(issuer, new Dictionary<string, object>
        {
            ["sub"] = "attacker", ["tenant_id"] = "tenant-a",
            ["roles"] = new[] { ChoRolePermissions.PlatformAdmin },
            ["permissions"] = new[] { "platform:admin", "claims:adjust" },
        });
        (await Get("/api/service-identity/platform", forged)).Should().Be(HttpStatusCode.Unauthorized);
        (await Get("/api/service-identity/tenant", forged)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ServiceIssuer_WithoutServiceRole_CannotPoseAsANamedServiceClient()
    {
        var forged = Forge(ChoDevelopmentAuth.ServiceIssuer, new Dictionary<string, object>
        {
            ["sub"] = "token-service", ["azp"] = "token-service", ["tenant_id"] = "cho-platform",
            ["roles"] = new[] { ChoRolePermissions.PlatformAdmin },
        });
        (await Get("/api/service-identity/identity", forged)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UserIssuer_StillIssuesUsers_AndServiceIssuerStillIssuesServices()
    {
        (await Get("/api/service-identity/platform", ChoDevelopmentAuth.UserToken("tenant-a", ChoRolePermissions.PlatformAdmin)))
            .Should().Be(HttpStatusCode.OK);
        (await Get("/api/service-identity/identity", Service("token-service"))).Should().Be(HttpStatusCode.OK);
        (await Get("/api/service-identity/enrollment", Workload("wf-enrollment-import"))).Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null, false, false, ChoIssuerKind.User)]
    [InlineData(null, true, false, ChoIssuerKind.Service)]
    [InlineData(null, false, true, ChoIssuerKind.Workload)]
    [InlineData(ChoIssuerKind.Service, false, false, ChoIssuerKind.Service)]
    [InlineData(ChoIssuerKind.Workload, false, true, ChoIssuerKind.Workload)]
    public void EffectiveKind_IsExplicitKindOrTheLegacyFlags(ChoIssuerKind? kind, bool service, bool workload, ChoIssuerKind expected)
        => new ChoTrustedIssuer { Kind = kind, AllowServiceRole = service, AllowWorkloadIdentity = workload }
            .EffectiveKind.Should().Be(expected);

    [Theory]
    [InlineData(ChoIssuerKind.User, true, false)]
    [InlineData(ChoIssuerKind.Workload, true, false)]
    [InlineData(ChoIssuerKind.Service, false, true)]
    [InlineData(ChoIssuerKind.User, false, true)]
    public void KindContradictingALegacyFlag_FailsValidation(ChoIssuerKind kind, bool service, bool workload)
    {
        var options = new ChoAuthOptions
        {
            Issuers = { new ChoTrustedIssuer { Issuer = "x", SymmetricKey = ChoDevelopmentAuth.SymmetricKey, Kind = kind, AllowServiceRole = service, AllowWorkloadIdentity = workload } },
        };
        ((Action)(() => options.Validate(allowSymmetricKeys: true))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SameIssuerTwice_FailsValidation()
    {
        var options = new ChoAuthOptions
        {
            Issuers =
            {
                new ChoTrustedIssuer { Issuer = "x", SymmetricKey = ChoDevelopmentAuth.SymmetricKey, Kind = ChoIssuerKind.User },
                new ChoTrustedIssuer { Issuer = "x", SymmetricKey = ChoDevelopmentAuth.SymmetricKey, Kind = ChoIssuerKind.Service },
            },
        };
        ((Action)(() => options.Validate(allowSymmetricKeys: true))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WorkloadTokenIssuer_RefusesBroadTokens()
    {
        var issuer = ChoDevelopmentAuth.WorkloadTokenIssuer();
        ((Action)(() => issuer.IssueWorkloadToken("claims-service", "tenant-a", ["claims:read"]))).Should().Throw<ArgumentException>();
        ((Action)(() => issuer.IssueWorkloadToken("wf-x", "tenant-a", []))).Should().Throw<ArgumentException>();
        ((Action)(() => issuer.IssueWorkloadToken("wf-x", "tenant-a", ["platform:tenants"]))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UserToken_NeverCarriesTheWorkloadRole()
    {
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("u", "tenant-a", [ChoWorkloadRole.Name, ChoServiceRole.Name, "ClaimsExaminer"]);
        new JsonWebToken(token).Claims.Where(c => c.Type == "roles").Select(c => c.Value).Should().Equal("ClaimsExaminer");
    }

    [Fact]
    public void IssuerAllowingBothServiceAndWorkloadIdentities_FailsValidation()
    {
        var options = new ChoAuthOptions
        {
            Issuers = { new ChoTrustedIssuer { Issuer = "x", SymmetricKey = ChoDevelopmentAuth.SymmetricKey, AllowServiceRole = true, AllowWorkloadIdentity = true } },
        };
        ((Action)(() => options.Validate(allowSymmetricKeys: true))).Should().Throw<InvalidOperationException>();
    }
}
