using System.Security.Claims;
using FhirService.Middleware;
using FhirService.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests.Identity;

/// <summary>
/// SEC-01 — tenant binding.
///
/// Tenant is authority, so where it comes from is a security decision. Before
/// this, the X-Tenant-ID header was consulted whenever a token carried no
/// tenant claim, which meant any authenticated caller whose issuer did not map
/// a tenant could name any tenant and be believed. These tests pin the rule
/// that replaced it: the header may fill a vacuum, never contradict a token.
/// </summary>
public class TenantBindingTests
{
    private static HttpContext Request(
        string? tokenTenant = null,
        string? headerTenant = null,
        string? devHeaderTenant = null)
    {
        var context = new DefaultHttpContext();

        if (tokenTenant != null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tenant_id", tokenTenant)], "Bearer"));
        }

        if (headerTenant != null) context.Request.Headers["X-Tenant-ID"] = headerTenant;
        if (devHeaderTenant != null) context.Request.Headers["X-Dev-Tenant-ID"] = devHeaderTenant;

        return context;
    }

    // ── The token wins ────────────────────────────────────────────────────────

    [Fact]
    public void ATokenTenant_IsAuthoritative()
    {
        var result = TenantMiddleware.ResolveTenant(
            Request(tokenTenant: "tenant-a"), caller: null, isDevelopmentHost: false);

        result.TenantId.Should().Be("tenant-a");
        result.Conflict.Should().BeFalse();
    }

    [Fact]
    public void AHeaderContradictingTheToken_IsAConflict_NotAnOverride()
    {
        // The request's own two statements of authority disagree. Preferring
        // either would be picking a winner between a signed claim and an
        // unsigned header.
        var result = TenantMiddleware.ResolveTenant(
            Request(tokenTenant: "tenant-a", headerTenant: "tenant-b"),
            caller: null, isDevelopmentHost: false);

        result.Conflict.Should().BeTrue();
        result.TenantId.Should().BeNull("a conflicted request resolves to no tenant at all");
    }

    [Fact]
    public void AHeaderEchoingTheToken_IsAccepted()
    {
        // Service-to-service hops legitimately forward the tenant they were
        // called with; agreeing is not a conflict.
        var result = TenantMiddleware.ResolveTenant(
            Request(tokenTenant: "tenant-a", headerTenant: "tenant-a"),
            caller: null, isDevelopmentHost: false);

        result.Conflict.Should().BeFalse();
        result.TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public void TheIssuersMappedTenantClaim_IsPreferredOverConventionalNames()
    {
        // A deployment whose IdP emits `org` rather than `tenant_id` is still
        // authenticated by its token rather than falling through to a header.
        var caller = new AuthenticatedCaller
        {
            Issuer = "https://idp.example.com",
            CallerType = SmartCallerType.User,
            Scopes = new HashSet<string>(),
            TenantClaim = "tenant-from-mapping",
        };

        TenantMiddleware.ResolveTenant(Request(headerTenant: "tenant-b"), caller, false)
            .Should().Match<TenantMiddleware.TenantResolution>(
                r => r.Conflict && r.TenantId == null);
    }

    // ── The header fills a vacuum only ────────────────────────────────────────

    [Fact]
    public void WithNoTokenTenant_TheServiceHeaderIsUsed()
    {
        // Internal service-to-service calls have no user token to carry a
        // tenant, and the mesh boundary is what authenticates them.
        TenantMiddleware.ResolveTenant(Request(headerTenant: "tenant-a"), null, false)
            .TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public void WithNothingAtAll_NoTenantResolves()
        => TenantMiddleware.ResolveTenant(Request(), null, false).TenantId.Should().BeNull();

    // ── The development header is development-only ────────────────────────────

    [Fact]
    public void TheDevTenantHeader_IsIgnoredOutsideDevelopment()
    {
        // Honouring this on a production host would be an unauthenticated
        // tenant selector.
        TenantMiddleware.ResolveTenant(
            Request(devHeaderTenant: "tenant-a"), null, isDevelopmentHost: false)
            .TenantId.Should().BeNull();
    }

    [Fact]
    public void TheDevTenantHeader_WorksInDevelopment()
        => TenantMiddleware.ResolveTenant(
            Request(devHeaderTenant: "tenant-a"), null, isDevelopmentHost: true)
            .TenantId.Should().Be("tenant-a");
}
