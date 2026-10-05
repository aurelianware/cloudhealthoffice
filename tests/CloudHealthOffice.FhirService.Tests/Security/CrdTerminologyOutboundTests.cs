using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// CRD translates SNOMED codes at terminology-service's $batch-translate, which
/// takes the tenant from the token. The call must name the caller's tenant in
/// X-Tenant-ID and carry: the CHO caller's own token; for a SMART caller,
/// fhir-service's service token for that tenant (never the SMART token); with
/// no caller, a service token minted for that tenant. Driven through the
/// "TerminologyService" client exactly as Program.cs builds it.
/// </summary>
public class CrdTerminologyOutboundTests : IClassFixture<FhirTestWebAppFactory>
{
    private const string Tenant = "tenant-crd";
    private readonly FhirTestWebAppFactory _factory;

    public CrdTerminologyOutboundTests(FhirTestWebAppFactory factory) => _factory = factory;

    private (WebApplicationFactory<Program> Host, BatchRecorder Recorder) Host()
    {
        var recorder = new BatchRecorder();
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddHttpClient("TerminologyService").ConfigurePrimaryHttpMessageHandler(() => recorder)));
        return (host, recorder);
    }

    private static HttpContext ChoCaller(string token)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "um-nurse"), new Claim(ChoClaimTypes.TenantId, Tenant)], "Bearer"));
        FhirCallerSchemes.Mark(http.User, FhirCallerSchemes.ChoMarker);
        http.Request.Headers.Authorization = "Bearer " + token;
        http.Items["TenantId"] = Tenant;
        return http;
    }

    private static HttpContext SmartCaller(string token)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "ehr-app"), new Claim("scope", "user/ServiceRequest.read")], "Smart"));
        FhirCallerSchemes.Mark(http.User, FhirCallerSchemes.SmartMarker);
        http.Request.Headers.Authorization = "Bearer " + token;
        http.Items["TenantId"] = Tenant;
        return http;
    }

    private static async Task TranslateAsync(WebApplicationFactory<Program> host, HttpContext? caller)
    {
        using var scope = host.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = caller;
        try
        {
            var crd = (CrdService)scope.ServiceProvider.GetRequiredService<ICrdService>();
            await crd.TranslateCodesAsync(
                [new CrdCoding { System = "http://snomed.info/sct", Code = "44054006" }], Tenant, CancellationToken.None);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    [Fact]
    public async Task NoCaller_BatchTranslate_CarriesTenantAndServiceTokenForIt()
    {
        var (host, recorder) = Host();
        using var _ = host;

        await TranslateAsync(host, caller: null);

        var sent = recorder.Last!;
        sent.RequestUri!.AbsolutePath.Should().EndWith("/fhir/ConceptMap/$batch-translate");
        sent.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(Tenant);
        sent.Headers.Authorization.Should().NotBeNull();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(sent.Headers.Authorization!.Parameter);
        jwt.Claims.Should().Contain(c => c.Type == ChoClaimTypes.TenantId && c.Value == Tenant);
    }

    [Fact]
    public async Task ChoCaller_BatchTranslate_CarriesCallerTenantAndForwardedToken()
    {
        var (host, recorder) = Host();
        using var _ = host;
        var token = ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.UMCoordinator);

        await TranslateAsync(host, ChoCaller(token));

        var sent = recorder.Last!;
        sent.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(Tenant);
        sent.Headers.Authorization!.Parameter.Should().Be(token);
    }

    [Fact]
    public async Task SmartCaller_BatchTranslate_CarriesFhirServiceTokenForCallerTenant_NeverTheSmartToken()
    {
        var (host, recorder) = Host();
        using var _ = host;
        const string smartToken = "smart-access-token";

        await TranslateAsync(host, SmartCaller(smartToken));

        var sent = recorder.Last!;
        sent.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(Tenant);
        sent.Headers.Authorization!.Parameter.Should().NotBe(smartToken);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(sent.Headers.Authorization.Parameter);
        jwt.Subject.Should().Be("fhir-service");
        jwt.Claims.Should().Contain(c => c.Type == ChoClaimTypes.TenantId && c.Value == Tenant);
        jwt.Claims.Should().Contain(c => c.Type == ChoClaimTypes.Role && c.Value == ChoServiceRole.Name);
    }

    private sealed class BatchRecorder : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = [];

        public HttpRequestMessage? Last
        {
            get { lock (_requests) return _requests.LastOrDefault(); }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }
}
