using System.Net;
using ClaimsService.Services;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.ClaimsService.Tests.Security;

/// <summary>
/// #1299 review B1. The examiner approval re-run (<c>ClaimsController.ResolvePendedClaim</c>)
/// runs inside the examiner's HTTP request, where the shared outbound handler
/// forwards the caller's own token. authorization-service's <c>/validate</c>
/// needs <c>authorizations:read</c>, which ClaimsExaminer and Supervisor do not
/// hold: the lookup came back 403 → Unavailable → the claim re-pended (409),
/// so only a TenantAdmin could clear a PA-required claim.
///
/// The prior-authorization lookup therefore always carries claims-service's
/// own service token, for the adjudication context's tenant, through the real
/// handler chain (<c>AddChoAuthentication</c> → <c>ChoOutboundTokenHandler</c>).
/// authorization-service accepting that token for <c>/validate</c> is pinned on
/// the producer side (<c>AuthorizationAuthenticationTests.Validate_ClaimsServiceServiceToken_IsAccepted</c>).
/// </summary>
public class PriorAuthLookupCarriesServiceTokenTests
{
    private const string Tenant = "tenant-claims";
    private const string ClientId = "claims-service";

    private static NoCallerHost Host() => new(ClientId, (services, outbound) =>
    {
        services.AddHttpClient(UpstreamClientNames.AuthorizationService,
                c => c.BaseAddress = new Uri("http://authorization-service"))
            .ConfigurePrimaryHttpMessageHandler(() => outbound);
        services.AddScoped<HttpAuthorizationValidationClient>();
    });

    /// <summary>The examiner's own request, as the approval re-run sees it.</summary>
    private static DefaultHttpContext ExaminerRequest(string tenant, string role)
    {
        var token = ChoDevelopmentAuth.UserToken(tenant, role);
        var http = new DefaultHttpContext();
        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("sub", "examiner-1")], "Bearer"));
        http.Request.Headers.Authorization = "Bearer " + token;
        http.Items["TenantId"] = tenant;
        return http;
    }

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsExaminer)]
    [InlineData(ChoRolePermissions.ClaimsSupervisor)]
    public async Task InsideAnExaminerRequest_ValidateCarriesTheClaimsServiceToken_NotTheExaminers(string role)
    {
        using var host = Host();
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = ExaminerRequest(Tenant, role);
        host.Outbound.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"authorizationNumber":"AUTH-1","isValid":true,"validatedMemberId":"MEM-1","memberMatches":true}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        using var scope = host.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<HttpAuthorizationValidationClient>()
            .ValidateAsync(Tenant, "AUTH-1", "99213", new DateTime(2026, 4, 15), "1234567890", memberId: "MEM-1");

        result.Status.Should().Be(AuthorizationLookupStatus.Valid);
        var sent = host.Outbound.Last!;
        sent.RequestUri!.AbsolutePath.Should().Be("/api/authorizations/AUTH-1/validate");
        sent.RequestUri.Query.Should().Contain("memberId=MEM-1");
        NoCallerHost.TokenOf(sent).Should().Be((Tenant, ClientId));
        sent.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(Tenant);
    }

    [Fact]
    public async Task WithoutACaller_ValidateCarriesTheClaimsServiceToken()
    {
        using var host = Host();
        using var scope = host.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<HttpAuthorizationValidationClient>()
            .ValidateAsync(Tenant, "AUTH-1", "99213", new DateTime(2026, 4, 15), "1234567890", memberId: "MEM-1");

        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    /// <summary>
    /// The service identity never crosses tenants: inside a request authenticated
    /// for one tenant, a lookup naming another tenant goes out with no token
    /// (authorization-service answers 401 → Unavailable → pend).
    /// </summary>
    [Fact]
    public async Task InsideARequest_ALookupForAnotherTenant_GetsNoServiceToken()
    {
        using var host = Host();
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext =
            ExaminerRequest("tenant-other", ChoRolePermissions.ClaimsExaminer);
        using var scope = host.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<HttpAuthorizationValidationClient>()
            .ValidateAsync(Tenant, "AUTH-1", "99213", new DateTime(2026, 4, 15), "1234567890", memberId: "MEM-1");

        host.Outbound.Last!.Headers.Authorization.Should().BeNull();
    }
}
