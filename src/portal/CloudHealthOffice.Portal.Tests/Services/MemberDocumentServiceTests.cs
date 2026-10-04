using System.Net;
using System.Security.Claims;
using System.Text;
using CloudHealthOffice.Portal.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;
using static CloudHealthOffice.Portal.Tests.Services.ChoTokenTestSupport;

namespace CloudHealthOffice.Portal.Tests.Services;

public class MemberDocumentServiceTests
{
    private const string BaseUrl = "http://member-document-service.test";

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:MemberDocumentService"] = BaseUrl })
        .Build();

    private sealed record SentPart(string? Name, string? FileName, string? ContentType);

    private static (MemberDocumentService Service, List<List<SentPart>> Uploads) CreateUploadService()
    {
        var uploads = new List<List<SentPart>>();
        var handler = new FakeHandler(request =>
        {
            // Read inside the handler: the service disposes the multipart body afterwards.
            var parts = ((MultipartFormDataContent)request.Content!)
                .Select(p => new SentPart(
                    p.Headers.ContentDisposition?.Name?.Trim('"'),
                    p.Headers.ContentDisposition?.FileName?.Trim('"'),
                    p.Headers.ContentType?.MediaType))
                .ToList();
            uploads.Add(parts);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"id":"doc-1"}""", Encoding.UTF8, "application/json"),
            };
        });
        return (new MemberDocumentService(new HttpClient(handler), Config(), NullLogger<MemberDocumentService>.Instance), uploads);
    }

    [Theory]
    [InlineData("card.pdf", "application/pdf", "application/pdf")]
    [InlineData("scan.png", "image/png", "image/png")]
    [InlineData("photo.jpg", "image/jpg", "image/jpeg")]
    [InlineData("letter.pdf", "", "application/pdf")]
    [InlineData("scan.TIF", "application/octet-stream", "image/tiff")]
    [InlineData("photo.jpeg", "", "image/jpeg")]
    public async Task Upload_SendsTheFilesRealType(string fileName, string browserType, string expected)
    {
        var (service, uploads) = CreateUploadService();

        var id = await service.UploadDocumentAsync(
            new MemberDocumentUploadRequest { MemberId = "m-1", Category = "IdCard", FileName = fileName, ContentType = browserType },
            new MemoryStream(new byte[] { 1, 2, 3 }));

        id.Should().Be("doc-1");
        var file = uploads.Should().ContainSingle().Subject.Single(p => p.Name == "file");
        file.ContentType.Should().Be(expected);
        file.FileName.Should().Be(fileName);
    }

    [Fact]
    public async Task Upload_DoesNotSendUploadedBy()
    {
        var (service, uploads) = CreateUploadService();

        await service.UploadDocumentAsync(
            new MemberDocumentUploadRequest { MemberId = "m-1", Category = "IdCard", FileName = "card.pdf", ContentType = "application/pdf" },
            new MemoryStream(new byte[] { 1 }));

        uploads.Single().Select(p => p.Name).Should().NotContain("UploadedBy");
        typeof(MemberDocumentUploadRequest).GetProperty("UploadedBy").Should().BeNull(
            "the uploader is the token subject; the service ignores any value sent");
    }

    [Theory]
    [InlineData("notes.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("page.pdf", "text/html")]
    [InlineData("run.exe", "application/octet-stream")]
    [InlineData("noextension", "")]
    [InlineData("image.gif", "image/gif")]
    [InlineData("vector.svg", "")]
    public async Task Upload_RefusesUnsupportedTypesWithAClearMessage_WithoutCallingTheService(string fileName, string browserType)
    {
        var (service, uploads) = CreateUploadService();

        var act = () => service.UploadDocumentAsync(
            new MemberDocumentUploadRequest { MemberId = "m-1", Category = "IdCard", FileName = fileName, ContentType = browserType },
            new MemoryStream(new byte[] { 1 }));

        (await act.Should().ThrowAsync<UnsupportedDocumentTypeException>())
            .Which.Message.Should().Be("This file type can't be uploaded. Member documents must be PDF, PNG, JPEG or TIFF files.");
        uploads.Should().BeEmpty();
    }

    [Fact]
    public void DownloadLink_IsThePortalPath_NotTheBackend()
    {
        var idCards = new IdCardService(new HttpClient(new FakeHandler(HttpStatusCode.OK)), Config(),
            NullLogger<IdCardService>.Instance);

        idCards.BuildDocumentDownloadUrl("3f2a9c1e-0000-4000-8000-000000000001")
            .Should().Be("/member-documents/3f2a9c1e-0000-4000-8000-000000000001/download");
        idCards.BuildDocumentDownloadUrl("a/b").Should().Be("/member-documents/a%2Fb/download");
    }

    [Fact]
    public async Task OpenDocumentContent_ReturnsRefusalStatusesWithoutThrowing()
    {
        var service = new MemberDocumentService(
            new HttpClient(new FakeHandler(HttpStatusCode.Forbidden)), Config(), NullLogger<MemberDocumentService>.Instance);

        using var content = await service.OpenDocumentContentAsync("doc-1");

        content.Succeeded.Should().BeFalse();
        content.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        content.Content.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToggleLegalHold_SendsTheReason(bool legalHold)
    {
        string? body = null;
        var handler = new FakeHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var service = new MemberDocumentService(new HttpClient(handler), Config(), NullLogger<MemberDocumentService>.Instance);

        await service.ToggleLegalHoldAsync("doc-1", legalHold, "  Litigation notice 2026-14  ");

        body.Should().Contain($"\"legalHold\":{legalHold.ToString().ToLowerInvariant()}");
        body.Should().Contain("\"reason\":\"Litigation notice 2026-14\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ToggleLegalHold_RefusesABlankReason_WithoutCallingTheService(string reason)
    {
        var calls = 0;
        var handler = new FakeHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); });
        var service = new MemberDocumentService(new HttpClient(handler), Config(), NullLogger<MemberDocumentService>.Instance);

        var act = () => service.ToggleLegalHoldAsync("doc-1", false, reason);

        await act.Should().ThrowAsync<ArgumentException>();
        calls.Should().Be(0);
    }
}

/// <summary>
/// Where <see cref="ChoTokenProvider"/> finds the user: the circuit's
/// AuthenticationStateProvider inside a circuit, the request's user outside one.
/// </summary>
public class ChoTokenProviderUserSourceTests
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly List<string?> _entraTokens = new();

    private ChoTokenProvider CreateProvider(AuthenticationStateProvider state, HttpContext? httpContext)
    {
        var tokenAcquisition = new Mock<ITokenAcquisition>();
        tokenAcquisition
            .Setup(t => t.GetAccessTokenForUserAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<ClaimsPrincipal?>(), It.IsAny<TokenAcquisitionOptions?>()))
            .ReturnsAsync((IEnumerable<string> _, string? _, string? _, ClaimsPrincipal? user, TokenAcquisitionOptions? _)
                => "entra-token-for-" + user?.FindFirst("oid")?.Value);

        var tokenService = new FakeHandler(request =>
        {
            var entra = request.Headers.Authorization?.Parameter;
            _entraTokens.Add(entra);
            var oid = entra?.Replace("entra-token-for-", "");
            return Json(HttpStatusCode.OK, ExchangeJson(accessToken: "cho-token-for-" + oid));
        });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:TokenService"] = TokenServiceUrl,
                ["TokenService:Scope"] = Scope,
                ["Authentication:Mode"] = "Entra",
            })
            .Build();

        return new ChoTokenProvider(
            state,
            new SingleHandlerHttpClientFactory(tokenService),
            _cache,
            configuration,
            new TestHostEnvironment("Production"),
            NullLogger<ChoTokenProvider>.Instance,
            tokenAcquisition.Object,
            Mock.Of<IChoReauthenticationHandler>(),
            httpContextAccessor: new HttpContextAccessor { HttpContext = httpContext });
    }

    private static HttpContext RequestFor(ClaimsPrincipal user) => new DefaultHttpContext { User = user };

    [Fact]
    public async Task OutsideACircuit_UsesTheRequestsUser()
    {
        var provider = CreateProvider(new ServerAuthenticationStateProvider(), RequestFor(EntraUser(oid: "oid-req")));

        var result = await provider.GetTokenAsync();

        result.Succeeded.Should().BeTrue();
        result.Token!.AccessToken.Should().Be("cho-token-for-oid-req");
    }

    [Fact]
    public async Task OutsideACircuit_AnonymousRequest_IsNotAuthenticated()
    {
        var provider = CreateProvider(new ServerAuthenticationStateProvider(), RequestFor(Anonymous()));

        var result = await provider.GetTokenAsync();

        result.Status.Should().Be(ChoTokenStatus.NotAuthenticated);
        _entraTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task InACircuit_TheCircuitsUserWins_EvenWhenAnHttpContextIsPresent()
    {
        var circuit = new ServerAuthenticationStateProvider();
        circuit.SetAuthenticationState(Task.FromResult(new AuthenticationState(EntraUser(oid: "oid-circuit"))));
        var provider = CreateProvider(circuit, RequestFor(EntraUser(oid: "oid-other")));

        var result = await provider.GetTokenAsync();

        result.Token!.AccessToken.Should().Be("cho-token-for-oid-circuit");
        _entraTokens.Should().Equal("entra-token-for-oid-circuit");
    }

    [Fact]
    public async Task RequestsFromDifferentUsers_ShareNoCachedToken()
    {
        // One request after the other (HttpContextAccessor holds the current request
        // in an AsyncLocal, so the two cannot be set up side by side here).
        var first = CreateProvider(new ServerAuthenticationStateProvider(), RequestFor(EntraUser(oid: "oid-a")));
        (await first.GetTokenAsync()).Token!.AccessToken.Should().Be("cho-token-for-oid-a");

        var second = CreateProvider(new ServerAuthenticationStateProvider(), RequestFor(EntraUser(oid: "oid-b")));
        (await second.GetTokenAsync()).Token!.AccessToken.Should().Be("cho-token-for-oid-b");
        _entraTokens.Should().Equal("entra-token-for-oid-a", "entra-token-for-oid-b");
    }
}
