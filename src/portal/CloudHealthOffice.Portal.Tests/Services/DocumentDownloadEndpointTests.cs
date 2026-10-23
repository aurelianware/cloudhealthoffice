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
/// GET /documents/{id}/download, the link the Documents panel gives the
/// browser, through the portal's real CHO token wiring
/// (<see cref="ChoTokenServiceRegistration.AddChoUserTokens"/>) in a plain HTTP
/// request. Only Entra and the two HTTP peers (token service, document-service)
/// are fakes.
///
/// <para>The subject travels in the query because the catalog refuses a
/// document that is not asked for through one; a request without it never
/// reaches the catalog.</para>
/// </summary>
public sealed class DocumentDownloadEndpointTests : IAsyncLifetime
{
    private const string DocumentServiceUrl = "http://document-service.test";
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
            ["Services:DocumentService"] = DocumentServiceUrl,
            ["TokenService:Scope"] = Scope,
            ["Authentication:Mode"] = "Entra",
        });

        builder.Services.AddAuthentication(TestCookieAuth.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestCookieAuth>(TestCookieAuth.SchemeName, _ => { });
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
        builder.Services.AddScoped<IDocumentCatalogService, DocumentCatalogService>();

        _app = builder.Build();
        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapDocumentDownload();
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

    private Task<HttpResponseMessage> Download(
        string id, string? oid = "oid-1",
        DocumentSubjectKind kind = DocumentSubjectKind.Claim, string subjectId = "CLM-1")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, DocumentDownloadEndpoint.PathFor(id, kind, subjectId));
        if (oid != null)
            request.Headers.Add(TestCookieAuth.UserHeader, oid);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> DownloadRaw(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestCookieAuth.UserHeader, "oid-1");
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
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task TheSubjectIsPassedThroughToTheCatalog()
    {
        await Download("doc-1", kind: DocumentSubjectKind.Appeal, subjectId: "APL-9");

        var sent = _backendRequests.Should().ContainSingle().Subject;
        sent.RequestUri!.ToString().Should().Be(
            $"{DocumentServiceUrl}/api/v1/documents/doc-1/content?subjectKind=Appeal&subjectId=APL-9");
    }

    [Fact]
    public async Task AttachesTheSignedInUsersChoToken()
    {
        var response = await Download("doc-1", oid: "oid-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _exchangeEntraTokens.Should().Equal("entra-token-for-oid-1");
        var sent = _backendRequests.Should().ContainSingle().Subject;
        sent.Headers.Authorization!.Parameter.Should().Be("cho-token-for-oid-1");
        sent.Headers.GetValues(ChoBearerTokenHandler.TenantHeader).Should().Equal("tenant-of-oid-1");
    }

    [Theory]
    [InlineData("/documents/doc-1/download")]                                   // no subject at all
    [InlineData("/documents/doc-1/download?subjectKind=Claim")]                 // no subject id
    [InlineData("/documents/doc-1/download?subjectId=CLM-1")]                   // no subject kind
    [InlineData("/documents/doc-1/download?subjectKind=Nonsense&subjectId=X")]  // not a subject kind
    [InlineData("/documents/doc-1/download?subjectKind=Claim&subjectId=a/b")]   // not one id token
    public async Task AMissingOrMalformedSubjectNeverReachesTheCatalog(string path)
    {
        var response = await DownloadRaw(path);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _backendRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task AMalformedDocumentIdNeverReachesTheCatalog()
    {
        var response = await DownloadRaw("/documents/..%2Fsecret/download?subjectKind=Claim&subjectId=CLM-1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _backendRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict, HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway)]
    public async Task TheCatalogsRefusalIsPassedOn(HttpStatusCode backend, HttpStatusCode expected)
    {
        _backend = _ => new HttpResponseMessage(backend);

        var response = await Download("doc-1");

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task AnUnrenderableTypeGoesOutAsOpaqueBytes()
    {
        _backend = _ =>
        {
            var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PdfBytes) };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return message;
        };

        var response = await Download("doc-1");

        // Never text/html: a stored document must not render in the browser.
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
    }

    [Fact]
    public async Task AnUnsafeFileNameFromTheCatalogIsNotUsed()
    {
        _backend = _ =>
        {
            var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PdfBytes) };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            message.Content.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("attachment") { FileName = "\"../../etc/passwd\"" };
            return message;
        };

        var response = await Download("doc-1");

        response.Content.Headers.ContentDisposition!.FileName!.Trim('"').Should().Be("doc-1.pdf");
    }
}
