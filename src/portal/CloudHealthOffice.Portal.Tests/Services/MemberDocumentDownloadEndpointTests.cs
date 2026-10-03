using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CloudHealthOffice.Portal.Infrastructure;
using CloudHealthOffice.Portal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using static CloudHealthOffice.Portal.Tests.Services.ChoTokenTestSupport;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// GET /member-documents/{id}/download through the portal's real CHO token wiring
/// (<see cref="ChoTokenServiceRegistration.AddChoUserTokens"/>) in a plain HTTP
/// request, with the circuit's <see cref="ServerAuthenticationStateProvider"/>
/// registered exactly as Blazor Server does (and never set, as outside a circuit).
/// Only Entra and the two HTTP peers (token service, member-document-service) are fakes.
/// </summary>
public sealed class MemberDocumentDownloadEndpointTests : IAsyncLifetime
{
    private const string MemberDocumentServiceUrl = "http://member-document-service.test";
    private static readonly byte[] PdfBytes = "%PDF-1.7\nfake pdf body"u8.ToArray();

    private readonly List<HttpRequestMessage> _backendRequests = new();
    private readonly List<string?> _exchangeEntraTokens = new();
    private Func<HttpRequestMessage, HttpResponseMessage> _backend = _ => Pdf();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:TokenService"] = TokenServiceUrl,
            ["Services:MemberDocumentService"] = MemberDocumentServiceUrl,
            ["TokenService:Scope"] = Scope,
            ["Authentication:Mode"] = "Entra",
        });

        builder.Services.AddAuthentication(TestCookieAuth.Scheme)
            .AddScheme<AuthenticationSchemeOptions, TestCookieAuth>(TestCookieAuth.Scheme, _ => { });
        builder.Services.AddAuthorization();

        // What AddServerSideBlazor registers: the circuit's provider. Outside a circuit
        // nothing calls SetAuthenticationState on it.
        builder.Services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();

        var tokenAcquisition = new Mock<ITokenAcquisition>();
        tokenAcquisition
            .Setup(t => t.GetAccessTokenForUserAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<ClaimsPrincipal?>(), It.IsAny<TokenAcquisitionOptions?>()))
            .ReturnsAsync((IEnumerable<string> _, string? _, string? _, ClaimsPrincipal? user, TokenAcquisitionOptions? _)
                => "entra-token-for-" + user?.FindFirst("oid")?.Value);
        builder.Services.AddSingleton(tokenAcquisition.Object);

        builder.Services.AddChoUserTokens();
        builder.Services.AddScoped<IChoReauthenticationHandler>(_ => Mock.Of<IChoReauthenticationHandler>());
        builder.Services.AddHttpClient(ChoTokenProvider.TokenServiceClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new FakeHandler(TokenService));
        builder.Services.AddHttpClient("default")
            .ConfigurePrimaryHttpMessageHandler(() => new FakeHandler(Backend));
        builder.Services.AddScoped<IMemberDocumentService, MemberDocumentService>();

        _app = builder.Build();
        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapMemberDocumentDownload();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private HttpResponseMessage TokenService(HttpRequestMessage request)
    {
        if (request.RequestUri!.AbsolutePath != "/v1/token/exchange")
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        var entraToken = request.Headers.Authorization?.Parameter;
        _exchangeEntraTokens.Add(entraToken);
        var oid = entraToken?.Replace("entra-token-for-", "");
        return Json(HttpStatusCode.OK, ExchangeJson(tenantId: "tenant-of-" + oid, accessToken: "cho-token-for-" + oid));
    }

    private HttpResponseMessage Backend(HttpRequestMessage request)
    {
        _backendRequests.Add(request);
        return _backend(request);
    }

    private static HttpResponseMessage Pdf(string fileName = "doc-1.pdf")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PdfBytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };
        return response;
    }

    private Task<HttpResponseMessage> Download(string id, string? oid = "oid-1")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, MemberDocumentDownloadEndpoint.PathFor(id));
        if (oid != null)
            request.Headers.Add(TestCookieAuth.UserHeader, oid);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task RequiresSignIn()
    {
        var response = await Download("doc-1", oid: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _backendRequests.Should().BeEmpty();
        _exchangeEntraTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamsTheDocumentAsANoStoreNoSniffAttachment()
    {
        var response = await Download("doc-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(PdfBytes);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName!.Trim('"').Should().Be("doc-1.pdf");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        _backendRequests.Should().ContainSingle()
            .Which.RequestUri!.ToString().Should().Be($"{MemberDocumentServiceUrl}/api/v1/member-documents/doc-1/content");
    }

    [Fact]
    public async Task AttachesTheSignedInUsersChoTokenOutsideACircuit()
    {
        var response = await Download("doc-1", oid: "oid-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _exchangeEntraTokens.Should().Equal("entra-token-for-oid-1");
        var sent = _backendRequests.Should().ContainSingle().Subject;
        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        sent.Headers.Authorization.Parameter.Should().Be("cho-token-for-oid-1");
        sent.Headers.GetValues(ChoBearerTokenHandler.TenantHeader).Should().Equal("tenant-of-oid-1");
    }

    [Fact]
    public async Task EachUserGetsTheirOwnToken_NoCrossUserCache()
    {
        (await Download("doc-1", oid: "oid-1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Download("doc-1", oid: "oid-2")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Download("doc-1", oid: "oid-1")).StatusCode.Should().Be(HttpStatusCode.OK);

        _backendRequests.Select(r => r.Headers.Authorization!.Parameter)
            .Should().Equal("cho-token-for-oid-1", "cho-token-for-oid-2", "cho-token-for-oid-1");
        // The first user's token is cached for that user only; the second exchanges its own.
        _exchangeEntraTokens.Should().Equal("entra-token-for-oid-1", "entra-token-for-oid-2");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task BackendRefusalsMapThrough(HttpStatusCode backendStatus)
    {
        _backend = _ => new HttpResponseMessage(backendStatus) { Content = new StringContent("refused") };

        var response = await Download("doc-1");

        response.StatusCode.Should().Be(backendStatus);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task OtherBackendFailuresAreBadGateway()
    {
        _backend = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        (await Download("doc-1")).StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Theory]
    [InlineData("doc.1")]
    [InlineData("a..b")]
    [InlineData("a b")]
    [InlineData("-leading-dash")]
    public async Task MalformedIdsAreRefusedWithoutACall(string id)
    {
        var response = await Download(id);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _backendRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task UnexpectedBackendTypesAndNamesAreNeutralised()
    {
        _backend = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<script>x</script>") };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileName = "../evil.html" };
            return response;
        };

        var download = await Download("doc-1");

        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        download.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        download.Content.Headers.ContentDisposition.FileName!.Trim('"').Should().Be("doc-1");
    }

    /// <summary>Stands in for the portal's cookie: authenticated when the test header names a user.</summary>
    private sealed class TestCookieAuth : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string Scheme = "TestCookie";
        public const string UserHeader = "X-Test-User";

        public TestCookieAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var oid) || string.IsNullOrEmpty(oid))
                return Task.FromResult(AuthenticateResult.NoResult());

            var principal = EntraUser(tid: "entra-tid-1", oid: oid.ToString());
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
        }
    }
}
