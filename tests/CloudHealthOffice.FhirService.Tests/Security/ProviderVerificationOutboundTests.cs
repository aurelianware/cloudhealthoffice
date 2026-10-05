using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using FhirService.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// provider-verification-service's integrity-score takes the tenant from the
/// token and answers 401 without one. The "ProviderVerificationService" client
/// that PasController ($submit) and ProviderDirectoryController use must carry
/// the caller's tenant, with the CHO caller's own token, or for a SMART caller
/// fhir-service's service token for that tenant (never the SMART token).
/// These drive the client exactly as Program.cs builds it, with the inbound
/// request in IHttpContextAccessor as it is inside a controller action.
/// </summary>
public class ProviderVerificationOutboundTests : IClassFixture<FhirTestWebAppFactory>
{
    private const string Tenant = "tenant-pv";
    private const string IntegrityPath = "api/v1/providers/1234567893/integrity-score?tier=Basic";

    private readonly FhirTestWebAppFactory _factory;

    public ProviderVerificationOutboundTests(FhirTestWebAppFactory factory) => _factory = factory;

    private (WebApplicationFactory<Program> Host, Recorder Recorder) Host(string clientName = "ProviderVerificationService")
    {
        var recorder = new Recorder();
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => recorder)));
        return (host, recorder);
    }

    private static HttpContext ChoCaller(string token)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "examiner"), new Claim(ChoClaimTypes.TenantId, Tenant)], "Bearer"));
        FhirCallerSchemes.Mark(http.User, FhirCallerSchemes.ChoMarker);
        http.Request.Headers.Authorization = "Bearer " + token;
        http.Items["TenantId"] = Tenant;
        return http;
    }

    private static HttpContext SmartCaller(string token)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "smart-app"), new Claim("scope", "user/Claim.write")], "Smart"));
        FhirCallerSchemes.Mark(http.User, FhirCallerSchemes.SmartMarker);
        http.Request.Headers.Authorization = "Bearer " + token;
        http.Items["TenantId"] = Tenant;
        return http;
    }

    private static async Task<HttpRequestMessage> SendAsync(
        WebApplicationFactory<Program> host, HttpContext caller, string clientName, string path)
    {
        using var scope = host.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = caller;
        try
        {
            var client = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
            using var response = await client.GetAsync(path);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
        return host.Services.GetRequiredService<Recorder>().Last!;
    }

    [Fact]
    public async Task ChoCaller_IntegrityScore_CarriesCallerTenantAndForwardedToken()
    {
        var (host, recorder) = Host();
        using var _ = host;
        var token = ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.UMCoordinator);

        await SendViaRecorder(host, recorder, ChoCaller(token));

        var sent = recorder.Last!;
        sent.RequestUri!.Host.Should().Be("provider-verification-service.cloudhealthoffice");
        sent.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(Tenant);
        sent.Headers.Authorization!.Parameter.Should().Be(token);
    }

    [Fact]
    public async Task SmartCaller_IntegrityScore_CarriesServiceTokenForCallerTenant_NeverTheSmartToken()
    {
        var (host, recorder) = Host();
        using var _ = host;
        const string smartToken = "smart-access-token";

        await SendViaRecorder(host, recorder, SmartCaller(smartToken));

        var sent = recorder.Last!;
        sent.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(Tenant);
        sent.Headers.Authorization.Should().NotBeNull();
        sent.Headers.Authorization!.Parameter.Should().NotBe(smartToken);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(sent.Headers.Authorization.Parameter);
        jwt.Issuer.Should().Be(ChoDevelopmentAuth.ServiceIssuer);
        jwt.Subject.Should().Be("fhir-service");
        jwt.Claims.Should().Contain(c => c.Type == ChoClaimTypes.TenantId && c.Value == Tenant);
        jwt.Claims.Should().Contain(c => c.Type == ChoClaimTypes.Role && c.Value == ChoServiceRole.Name);
    }

    [Theory]
    [InlineData("http://wiremock/api")]            // dot-less, configured nowhere
    [InlineData("http://localhost:5020/api")]
    [InlineData("https://npiregistry.cms.hhs.gov/api/")]
    public async Task SmartCaller_NonChoHost_GetsNoServiceTokenAndNoSmartToken(string url)
    {
        var (host, recorder) = Host("NppesApi");
        using var _ = host;

        await SendViaRecorder(host, recorder, SmartCaller("smart-access-token"), "NppesApi", url);

        recorder.Last!.Headers.Authorization.Should().BeNull();
    }

    private static async Task SendViaRecorder(
        WebApplicationFactory<Program> host, Recorder recorder, HttpContext caller,
        string clientName = "ProviderVerificationService", string path = IntegrityPath)
    {
        _ = recorder;
        using var scope = host.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = caller;
        try
        {
            var client = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    internal sealed class Recorder : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = [];

        public HttpRequestMessage? Last
        {
            get { lock (_requests) return _requests.LastOrDefault(); }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"compositeScore":90,"rating":"Clear","status":"Verified"}"""),
            });
        }
    }
}
