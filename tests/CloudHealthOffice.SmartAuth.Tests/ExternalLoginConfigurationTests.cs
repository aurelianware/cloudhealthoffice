using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SmartAuthService.Services;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>SmartAuth:ExternalLogin is validated at startup, and the ID-token → session mapping.</summary>
[Collection(SmartAuthCollection.Name)]
public class ExternalLoginConfigurationTests
{
    private static readonly Uri Issuer = new("https://auth.cloudhealthoffice.com");
    private readonly SmartAuthTestFixture _fixture;

    public ExternalLoginConfigurationTests(SmartAuthTestFixture fixture) => _fixture = fixture;

    private static Dictionary<string, string?> Valid() => new()
    {
        ["SmartAuth:ExternalLogin:Enabled"] = "true",
        ["SmartAuth:ExternalLogin:Authority"] = "https://cho.ciamlogin.com/tid/v2.0",
        ["SmartAuth:ExternalLogin:ClientId"] = "client",
        ["SmartAuth:ExternalLogin:ClientSecret"] = "secret",
        ["SmartAuth:ExternalLogin:ExpectedIssuer"] = "https://tid.ciamlogin.com/tid/v2.0",
        ["SmartAuth:ExternalLogin:DisplayName"] = "CHO ID",
    };

    private static ExternalLoginOptions? Resolve(Dictionary<string, string?> values, string environment = "Production")
        => ExternalLogin.Resolve(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new Env(environment), Issuer);

    [Fact]
    public void Disabled_ResolvesToNull_WhateverElseIsSet()
    {
        var values = Valid();
        values["SmartAuth:ExternalLogin:Enabled"] = "false";
        values["SmartAuth:ExternalLogin:Authority"] = "http://not-checked";
        Resolve(values).Should().BeNull();
        Resolve([]).Should().BeNull("disabled is the default");
    }

    [Fact]
    public void Valid_Resolves_WithTheRedirectUriBuiltFromTheIssuer()
    {
        var options = Resolve(Valid())!;
        options.RedirectUri.Should().Be(new Uri("https://auth.cloudhealthoffice.com/signin-oidc"));
        options.ExpectedIssuer.Should().Be("https://tid.ciamlogin.com/tid/v2.0");

        var custom = Valid();
        custom["SmartAuth:ExternalLogin:CallbackPath"] = "/account/callback";
        Resolve(custom)!.RedirectUri.Should().Be(new Uri("https://auth.cloudhealthoffice.com/account/callback"));
    }

    [Theory]
    [InlineData("Authority")]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("ExpectedIssuer")]
    [InlineData("DisplayName")]
    public void Enabled_WithAMissingValue_RefusesToStart(string key)
    {
        var values = Valid();
        values["SmartAuth:ExternalLogin:" + key] = " ";
        var act = () => Resolve(values);
        act.Should().Throw<InvalidOperationException>().WithMessage($"*SmartAuth:ExternalLogin:{key}*");
    }

    [Theory]
    [InlineData("Authority", "http://cho.ciamlogin.com/tid/v2.0")]
    [InlineData("ExpectedIssuer", "http://tid.ciamlogin.com/tid/v2.0")]
    [InlineData("Authority", "not a uri")]
    [InlineData("Authority", "https://cho.ciamlogin.com/tid/v2.0?p=x")]
    [InlineData("CallbackPath", "signin-oidc")]
    [InlineData("CallbackPath", "//evil.example/cb")]
    public void Enabled_WithAnUnsafeValue_RefusesToStart(string key, string value)
    {
        var values = Valid();
        values["SmartAuth:ExternalLogin:" + key] = value;
        var act = () => Resolve(values);
        act.Should().Throw<InvalidOperationException>().WithMessage($"*SmartAuth:ExternalLogin:{key}*");
    }

    [Fact]
    public void HttpAuthority_IsAllowed_OnlyInDevelopment()
    {
        var values = Valid();
        values["SmartAuth:ExternalLogin:Authority"] = "http://localhost:8081/realms/dev";
        Resolve(values, "Development").Should().NotBeNull();
        var act = () => Resolve(values, "Testing");
        act.Should().Throw<InvalidOperationException>().WithMessage("*HTTPS*");
    }

    [Fact]
    public void Host_EnabledWithoutAClientSecret_FailsToStart()
    {
        var host = _fixture.Factory.WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in Valid())
                b.UseSetting(k, k.EndsWith("ClientSecret") ? "" : v);
        });
        var act = () => host.CreateClient();
        act.Should().Throw<InvalidOperationException>().WithMessage("*SmartAuth:ExternalLogin:ClientSecret*");
    }

    // ── ID token → session ────────────────────────────────────────────────────

    private const string Iss = "https://tid.ciamlogin.com/tid/v2.0";

    private static ClaimsPrincipal IdToken(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value, null, Iss)), "oidc"));

    [Fact]
    public void Map_KeepsIssuerAndOid_AndDropsEverythingElse()
    {
        var (session, refusal) = ExternalLogin.MapSession(Iss, IdToken(
            ("oid", "oid-1"), ("sub", "sub-1"), ("tid", "entra-tenant"), ("tenant_id", "t-1"),
            ("roles", "TenantAdmin"), ("email", "a@b.c"), ("preferred_username", "a@b.c"),
            ("extension_MemberId", "pat-001"), (ClaimTypes.Role, "admin"), ("cho_idp_iss", "urn:forged")), Iss);

        refusal.Should().BeNull();
        session!.Claims.Select(c => (c.Type, c.Value)).Should().BeEquivalentTo(new[]
        {
            (SmartSession.IssuerClaim, Iss),
            (ClaimTypes.NameIdentifier, "oid-1"),
        });
    }

    [Fact]
    public void Map_UsesSub_OnlyWithoutOid_AndNeverEmailOrUsername()
    {
        ExternalLogin.MapSession(Iss, IdToken(("sub", "sub-1"), ("email", "a@b.c")), Iss)
            .Session!.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be("sub-1");

        var (session, refusal) = ExternalLogin.MapSession(Iss, IdToken(("email", "a@b.c"), ("preferred_username", "a")), Iss);
        session.Should().BeNull();
        refusal.Should().Be("id_token_has_no_subject");
    }

    [Theory]
    [InlineData("https://tid.ciamlogin.com/tid/v2.0/")]
    [InlineData("https://TID.ciamlogin.com/tid/v2.0")]
    [InlineData("https://other.ciamlogin.com/other/v2.0")]
    [InlineData("")]
    public void Map_RefusesAnyIssuerButTheExpectedOne_Exactly(string tokenIssuer)
    {
        var (session, refusal) = ExternalLogin.MapSession(tokenIssuer, IdToken(("oid", "oid-1")), Iss);
        session.Should().BeNull();
        refusal.Should().Be("id_token_issuer_not_expected");
    }

    [Fact]
    public void DevelopmentLogin_AndExternalLogin_ProduceTheSameShape()
    {
        var dev = SmartAuthService.Controllers.DevelopmentLogin.Principal("alice");
        var ext = ExternalLogin.MapSession(Iss, IdToken(("oid", "alice"), ("name", "alice")), Iss).Session!;
        dev.Claims.Select(c => c.Type).Should().BeEquivalentTo(ext.Claims.Select(c => c.Type));
        dev.Identity!.AuthenticationType.Should().Be(ext.Identity!.AuthenticationType);
        SmartSession.IdentityOf(ext).Should().Be(new SmartAuthService.Models.SmartIdentity(Iss, "alice"));
    }

    internal sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "smart-auth-service";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
