using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Security;

public class ChoOutboundTokenHandlerTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpMessageInvoker, Capture) Build(HttpContext? http, bool withServiceToken)
    {
        var services = new ServiceCollection();
        var options = new ChoAuthOptions { Audience = ChoDevelopmentAuth.Audience };
        if (withServiceToken)
        {
            options.ServiceToken = new ChoServiceTokenOptions
            {
                Issuer = ChoDevelopmentAuth.ServiceIssuer, ClientId = "claims-service",
                SymmetricKey = ChoDevelopmentAuth.SymmetricKey,
            };
            services.AddSingleton(ChoDevelopmentAuth.ServiceTokenIssuer());
        }
        services.AddSingleton(options);

        var capture = new Capture();
        var handler = new ChoOutboundTokenHandler(
            new HttpContextAccessor { HttpContext = http }, services.BuildServiceProvider(),
            NullLogger<ChoOutboundTokenHandler>.Instance)
        { InnerHandler = capture };
        return (new HttpMessageInvoker(handler), capture);
    }

    [Fact]
    public async Task ForwardsInboundUserToken_AndEchoesAuthenticatedTenant()
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer"));
        http.Request.Headers.Authorization = "Bearer user-token";
        http.Items["TenantId"] = "tenant-a";
        var (invoker, capture) = Build(http, withServiceToken: true);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://member-service.cloudhealthoffice/api/members");
        request.Headers.Add("X-Tenant-ID", "tenant-b");
        await invoker.SendAsync(request, default);

        capture.Last!.Headers.Authorization!.Parameter.Should().Be("user-token");
        capture.Last.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("tenant-a");
    }

    [Fact]
    public async Task WithoutUserContext_MintsServiceTokenForNamedTenant()
    {
        var (invoker, capture) = Build(http: null, withServiceToken: true);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://member-service/api/members");
        request.Headers.Add("X-Tenant-ID", "tenant-a");
        await invoker.SendAsync(request, default);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(capture.Last!.Headers.Authorization!.Parameter);
        jwt.Claims.Single(c => c.Type == "tenant_id").Value.Should().Be("tenant-a");
        jwt.Claims.Single(c => c.Type == "sub").Value.Should().Be("claims-service");
        jwt.Audiences.Should().ContainSingle(ChoDevelopmentAuth.Audience);
    }

    [Fact]
    public async Task NeverSendsTokensToExternalHosts()
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer"));
        http.Request.Headers.Authorization = "Bearer user-token";
        var (invoker, capture) = Build(http, withServiceToken: true);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"), default);

        capture.Last!.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task NoUserAndNoServiceToken_SendsNoCredentials()
    {
        var (invoker, capture) = Build(http: null, withServiceToken: false);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://member-service/api/members");
        request.Headers.Add("X-Tenant-ID", "tenant-a");
        await invoker.SendAsync(request, default);

        capture.Last!.Headers.Authorization.Should().BeNull();
    }
}
