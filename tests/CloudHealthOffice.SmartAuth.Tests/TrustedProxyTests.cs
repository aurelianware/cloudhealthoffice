using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Server.AspNetCore;
using SmartAuthService.Services;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// In production TLS ends at the ingress and the pod sees plain HTTP. These
/// hosts keep OpenIddict's HTTPS requirement ON (the other suites turn it off)
/// to show the endpoints work only when a trusted ingress says the client used
/// HTTPS.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class TrustedProxyTests
{
    private const string IngressNetwork = "10.244.0.0/16";
    private static readonly IPAddress IngressPod = IPAddress.Parse("10.244.3.17");
    private static readonly IPAddress OtherPod = IPAddress.Parse("10.10.0.5");

    private readonly SmartAuthTestFixture _fixture;

    public TrustedProxyTests(SmartAuthTestFixture fixture) => _fixture = fixture;

    /// <summary>Sets the connection's remote address the way Kestrel would, before the app's pipeline.</summary>
    private sealed class RemoteAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private WebApplicationFactory<SmartAuthService.Program> Host(IPAddress from, string? trustedNetwork = IngressNetwork)
        => _fixture.Factory.WithWebHostBuilder(b =>
        {
            if (trustedNetwork != null)
                b.UseSetting($"{TrustedProxy.ConfigKey}:0", trustedNetwork);
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new RemoteAddress(from));
                // Undo the fixture's override: the production HTTPS requirement applies.
                services.PostConfigure<OpenIddictServerAspNetCoreOptions>(o => o.DisableTransportSecurityRequirement = false);
            });
        });

    private static Task<HttpResponseMessage> Discovery(WebApplicationFactory<SmartAuthService.Program> host, string? forwardedProto)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        if (forwardedProto != null)
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        return host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost"),
        }).SendAsync(request);
    }

    [Fact]
    public async Task HttpFromTheIngress_WithForwardedProtoHttps_IsServed()
    {
        await using var host = Host(IngressPod);

        var response = await Discovery(host, "https");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForwardedProtoFromAnUntrustedAddress_IsIgnored_AndPlainHttpIsRefused()
    {
        await using var host = Host(OtherPod);

        var response = await Discovery(host, "https");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NoTrustedNetworksConfigured_TrustsNoForwardedHeader()
    {
        await using var host = Host(IngressPod, trustedNetwork: null);

        var response = await Discovery(host, "https");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PlainHttpFromTheIngress_WithoutForwardedProto_IsRefused()
    {
        await using var host = Host(IngressPod);

        var response = await Discovery(host, forwardedProto: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("10.244.0.0")]
    [InlineData("10.244.0.0/33")]
    [InlineData("not-an-ip/16")]
    public void InvalidNetwork_FailsStartup(string entry)
    {
        var act = () => TrustedProxy.Parse([entry]);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{TrustedProxy.ConfigKey}*");
    }
}
