using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using static CloudHealthOffice.Portal.Tests.Services.ChoTokenTestSupport;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>Redeeming an invitation through the token service.</summary>
public class ChoTokenProviderInvitationTests
{
    private const string Code = "Zk3c9QmT2xY7bL0pN4vR8sW1aE6dH5jU3fG2kM9nB0q";

    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IDistributedCache _preferences =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly Mock<ITokenAcquisition> _tokenAcquisition = new();
    private readonly ListLogger<ChoTokenProvider> _logger = new();
    private readonly List<HttpRequestMessage> _redeemCalls = new();
    private readonly List<string> _redeemBodies = new();
    private readonly List<HttpRequestMessage> _exchangeCalls = new();
    private Func<HttpResponseMessage> _redeemResponse =
        () => Json(HttpStatusCode.OK, ExchangeJson(tenantId: "tenant-invited", accessToken: "cho-invited", expiresIn: 300));

    public ChoTokenProviderInvitationTests()
    {
        _tokenAcquisition
            .Setup(t => t.GetAccessTokenForUserAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<ClaimsPrincipal?>(), It.IsAny<TokenAcquisitionOptions?>()))
            .ReturnsAsync("entra-token");
    }

    private ChoTokenProvider CreateProvider(ClaimsPrincipal? user = null)
    {
        var handler = new FakeHandler(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/invitations/redeem":
                    _redeemCalls.Add(request);
                    _redeemBodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    return _redeemResponse();
                case "/v1/token/exchange":
                    _exchangeCalls.Add(request);
                    return Json(HttpStatusCode.OK, ExchangeJson());
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
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
            new StaticAuthenticationStateProvider(user ?? EntraUser()),
            new SingleHandlerHttpClientFactory(handler),
            _cache, configuration, new TestHostEnvironment("Production"), _logger,
            _tokenAcquisition.Object, null, _preferences);
    }

    [Fact]
    public async Task Redeem_PostsTheCodeWithTheEntraToken_AndMakesTheInvitedTenantCurrent()
    {
        var sut = CreateProvider();

        var result = await sut.RedeemInvitationAsync(Code);

        result.Status.Should().Be(ChoInvitationStatus.Success);
        result.Token!.TenantId.Should().Be("tenant-invited");
        var call = _redeemCalls.Should().ContainSingle().Subject;
        call.Method.Should().Be(HttpMethod.Post);
        call.RequestUri!.AbsoluteUri.Should().Be($"{TokenServiceUrl}/v1/invitations/redeem");
        call.RequestUri.Query.Should().BeEmpty("the code travels in the body only");
        call.Headers.Authorization!.Parameter.Should().Be("entra-token");
        JsonDocument.Parse(_redeemBodies.Single()).RootElement.GetProperty("code").GetString().Should().Be(Code);

        // The invited tenant is current, and its token is used without another exchange.
        sut.CurrentTenantId.Should().Be("tenant-invited");
        var token = await sut.GetTokenAsync();
        token.Token!.AccessToken.Should().Be("cho-invited");
        _exchangeCalls.Should().BeEmpty();

        // A new circuit (after the page reloads) starts in the invited tenant too.
        var next = CreateProvider();
        (await next.GetTokenAsync()).Token!.TenantId.Should().Be("tenant-invited");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"not_found"}""", ChoInvitationStatus.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_request"}""", ChoInvitationStatus.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"expired"}""", ChoInvitationStatus.Expired)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"revoked"}""", ChoInvitationStatus.Revoked)]
    [InlineData(HttpStatusCode.Conflict, """{"error":"already_redeemed"}""", ChoInvitationStatus.AlreadyRedeemed)]
    [InlineData(HttpStatusCode.Conflict, """{"error":"identity_in_use"}""", ChoInvitationStatus.IdentityInUse)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"no_access"}""", ChoInvitationStatus.NoAccess)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""", ChoInvitationStatus.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""", ChoInvitationStatus.InvalidToken)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":"unavailable"}""", ChoInvitationStatus.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, "oops", ChoInvitationStatus.Unavailable)]
    public async Task Redeem_MapsRefusals_AndKeepsTheCurrentTenant(HttpStatusCode status, string body, ChoInvitationStatus expected)
    {
        _redeemResponse = () => Json(status, body);
        var sut = CreateProvider();

        var result = await sut.RedeemInvitationAsync(Code);

        result.Status.Should().Be(expected);
        result.Token.Should().BeNull();
        sut.CurrentTenantId.Should().BeNull();
    }

    [Fact]
    public async Task Redeem_EmailMismatch_CarriesTheMaskedInvitedAddress()
    {
        _redeemResponse = () => Json(HttpStatusCode.Forbidden, """{"error":"email_mismatch","invitedEmail":"p***@acme.com"}""");

        var result = await CreateProvider().RedeemInvitationAsync(Code);

        result.Status.Should().Be(ChoInvitationStatus.EmailMismatch);
        result.InvitedEmail.Should().Be("p***@acme.com");
    }

    [Fact]
    public async Task Redeem_NotSignedIn_CallsNothing()
    {
        var result = await CreateProvider(Anonymous()).RedeemInvitationAsync(Code);

        result.Status.Should().Be(ChoInvitationStatus.NotAuthenticated);
        _redeemCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Redeem_NeverLogsTheCode()
    {
        var sut = CreateProvider();
        await sut.RedeemInvitationAsync(Code);
        _redeemResponse = () => Json(HttpStatusCode.BadRequest, """{"error":"expired"}""");
        await sut.RedeemInvitationAsync(Code);
        _redeemResponse = () => throw new HttpRequestException("boom " + Code);
        await sut.RedeemInvitationAsync(Code);

        _logger.Messages.Should().NotBeEmpty();
        _logger.Messages.Should().NotContain(m => m.Contains(Code));
    }
}

/// <summary>Captures formatted log messages (and exception text).</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _messages = new();
    public IReadOnlyList<string> Messages => _messages.ToArray();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _messages.Enqueue(formatter(state, exception) + " " + exception);
}
