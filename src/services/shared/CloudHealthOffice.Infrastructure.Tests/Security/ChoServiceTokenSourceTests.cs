using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.Infrastructure.Tests.Security;

/// <summary>
/// Where a service's own service tokens come from: token-service by workload
/// identity (deployed), or a local development key (Development/Testing only).
/// </summary>
public sealed class ChoServiceTokenSourceTests
{
    private const string Scope = "api://5e1f0000-0000-0000-0000-00000000a5c1/.default";

    // ── TokenService source: exchange, cache, refresh ───────────────────

    [Fact]
    public async Task Exchanges_the_workload_identity_token_at_token_service_and_caches_per_tenant()
    {
        var (source, entra, tokenService, _) = Build();

        var first = await source.GetTokenAsync("acme");
        var again = await source.GetTokenAsync("acme");
        var other = await source.GetTokenAsync("beta");

        first.Should().Be("svc-token-1").And.Be(again);
        other.Should().Be("svc-token-2");
        tokenService.Requests.Should().HaveCount(2);
        entra.Scopes.Should().OnlyContain(s => s == Scope);

        var request = tokenService.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be(new Uri("http://token-service/v1/token/service"));
        request.Authorization.Should().Be("Bearer entra-token-1");
        request.TenantId.Should().Be("acme");
    }

    [Fact]
    public async Task Refreshes_shortly_before_expiry()
    {
        var (source, _, tokenService, clock) = Build(expiresIn: 300);

        (await source.GetTokenAsync("acme")).Should().Be("svc-token-1");
        clock.Advance(TimeSpan.FromMinutes(3.9)); // RefreshBefore is 1 minute
        (await source.GetTokenAsync("acme")).Should().Be("svc-token-1");
        clock.Advance(TimeSpan.FromMinutes(0.2));
        (await source.GetTokenAsync("acme")).Should().Be("svc-token-2");
        tokenService.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_tenant_share_one_exchange()
    {
        var (source, _, tokenService, _) = Build(delay: TimeSpan.FromMilliseconds(50));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => source.GetTokenAsync("acme").AsTask()));

        tokens.Distinct().Should().ContainSingle();
        tokenService.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_token_for_another_client_or_tenant_is_never_used()
    {
        var (source, _, _, _) = Build(clientIdInResponse: "capitation-service");
        await FluentActions.Awaiting(() => source.GetTokenAsync("acme").AsTask())
            .Should().ThrowAsync<ChoServiceTokenUnavailableException>();

        (source, _, _, _) = Build(tenantInResponse: "beta");
        await FluentActions.Awaiting(() => source.GetTokenAsync("acme").AsTask())
            .Should().ThrowAsync<ChoServiceTokenUnavailableException>();
    }

    [Fact]
    public async Task A_refusal_or_an_unreachable_entra_is_unavailable_not_cached()
    {
        var (source, _, tokenService, _) = Build(status: HttpStatusCode.Forbidden);
        await FluentActions.Awaiting(() => source.GetTokenAsync("acme").AsTask())
            .Should().ThrowAsync<ChoServiceTokenUnavailableException>();
        tokenService.Status = HttpStatusCode.OK;
        (await source.GetTokenAsync("acme")).Should().StartWith("svc-token-");

        var (noEntra, _, _, _) = Build(entraFails: true);
        var thrown = await FluentActions.Awaiting(() => noEntra.GetTokenAsync("acme").AsTask())
            .Should().ThrowAsync<ChoServiceTokenUnavailableException>();
        thrown.Which.Should().BeAssignableTo<HttpRequestException>("callers treat it as an unavailable downstream");
    }

    // ── Selection and the LocalKey restriction ──────────────────────────

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Local_key_is_refused_outside_development_and_testing(string environment)
    {
        foreach (var key in new[] { ("PrivateKeyPem", PrivatePem()), ("SymmetricKey", ChoDevelopmentAuth.SymmetricKey) })
        {
            var settings = Trust();
            settings["ChoAuth:ServiceToken:Source"] = "LocalKey";
            settings["ChoAuth:ServiceToken:Issuer"] = "cho-internal";
            settings["ChoAuth:ServiceToken:ClientId"] = "claims-service";
            settings["ChoAuth:ServiceToken:" + key.Item1] = key.Item2;

            var act = () => new ServiceCollection().AddChoAuthentication(Config(settings), new Env(environment));
            act.Should().Throw<InvalidOperationException>().WithMessage("*LocalKey*Development*");
        }
    }

    [Fact]
    public void Source_defaults_to_local_key_without_a_token_service_url()
    {
        // The shipped Development configuration (no Source): refused once deployed.
        var settings = Trust();
        settings["ChoAuth:ServiceToken:Issuer"] = "cho-internal";
        settings["ChoAuth:ServiceToken:ClientId"] = "claims-service";
        settings["ChoAuth:ServiceToken:PrivateKeyPem"] = PrivatePem();
        var act = () => new ServiceCollection().AddChoAuthentication(Config(settings), new Env("Production"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Local_key_is_permitted_in_development_and_testing(string environment)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChoAuthentication(
            Config(new Dictionary<string, string?>(ChoDevelopmentAuth.Configuration("claims-service"))), new Env(environment));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IChoServiceTokenSource>().Should().BeOfType<LocalKeyChoServiceTokenSource>();
    }

    [Theory]
    [InlineData(null, Scope, "a key")]
    [InlineData("http://token-service", null, "no scope")]
    [InlineData("http://token-service", "api://x/Cho.Token", "a delegated scope")]
    [InlineData("token-service", Scope, "a relative url")]
    public void Incomplete_token_service_source_fails_startup(string? url, string? scope, string _)
    {
        var settings = Trust();
        settings["ChoAuth:ServiceToken:Source"] = "TokenService";
        settings["ChoAuth:ServiceToken:ClientId"] = "claims-service";
        settings["ChoAuth:ServiceToken:TokenServiceUrl"] = url ?? "http://token-service";
        settings["ChoAuth:ServiceToken:EntraScope"] = scope;
        if (url == null)
            settings["ChoAuth:ServiceToken:PrivateKeyPem"] = PrivatePem();

        var act = () => new ServiceCollection().AddChoAuthentication(Config(settings), new Env("Production"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Production_host_sends_background_calls_with_the_token_service_token()
    {
        var tokenService = new FakeTokenService();
        var settings = Trust();
        settings["ChoAuth:ServiceToken:Source"] = "TokenService";
        settings["ChoAuth:ServiceToken:ClientId"] = "claims-service";
        settings["ChoAuth:ServiceToken:TokenServiceUrl"] = "http://token-service";
        settings["ChoAuth:ServiceToken:EntraScope"] = Scope;
        settings["Services:MemberService"] = "http://member-service";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(Config(settings));
        services.AddSingleton(new ChoServiceTokenCredential(new FakeEntra()));
        services.AddChoAuthentication(Config(settings), new Env("Production"));
        services.AddHttpClient(TokenServiceChoServiceTokenSource.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => tokenService);
        var member = new Capture();
        services.AddHttpClient("member").ConfigurePrimaryHttpMessageHandler(() => member);

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IChoServiceTokenSource>().Should().BeOfType<TokenServiceChoServiceTokenSource>();
        provider.GetService<ChoTokenIssuer>().Should().BeNull("a deployed service holds no signing key");

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("member");
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://member-service/api/v1/members");
        request.Headers.Add("X-Tenant-ID", "acme");
        await client.SendAsync(request);

        member.Authorization.Should().Be("Bearer svc-token-1");
        // The token-service call carried the Entra token only, never a CHO token.
        tokenService.Requests.Should().ContainSingle().Which.Authorization.Should().Be("Bearer entra-token-1");
    }

    // ── Callees ─────────────────────────────────────────────────────────

    [Fact]
    public void A_sparse_issuer_index_adds_an_issuer_beside_the_existing_ones()
    {
        // The shared cho-service-tokens ConfigMap adds token-service's service
        // issuer as ChoAuth__Issuers__9__* next to issuers 0..n supplied elsewhere.
        var settings = Trust();
        settings["ChoAuth:Issuers:1:Issuer"] = "cho-token-service";
        settings["ChoAuth:Issuers:1:PublicKeyPem"] = PublicPem(PrivatePem());
        settings["ChoAuth:Issuers:9:Issuer"] = "cho-token-service-svc-2";
        settings["ChoAuth:Issuers:9:PublicKeyPem"] = PublicPem(PrivatePem());
        settings["ChoAuth:Issuers:9:Kind"] = "Service";
        var options = new ChoAuthOptions();
        Config(settings).GetSection(ChoAuthOptions.SectionName).Bind(options);

        options.Issuers.Select(i => i.Issuer).Should().Equal("cho-token-service-svc", "cho-token-service", "cho-token-service-svc-2");
        options.Validate(allowSymmetricKeys: false);
    }

    [Fact]
    public async Task Callee_trusting_only_token_service_rejects_a_token_signed_with_the_old_shared_key()
    {
        var tokenServiceKey = PrivatePem();
        var sharedKey = PrivatePem();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ChoAuth:Audience"] = "cho-api",
            ["ChoAuth:Issuers:0:Issuer"] = "cho-token-service-svc",
            ["ChoAuth:Issuers:0:PublicKeyPem"] = PublicPem(tokenServiceKey),
            ["ChoAuth:Issuers:0:Kind"] = "Service",
        });
        builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment);
        await using var app = builder.Build();
        app.UseChoAuthentication();
        app.MapGet("/svc", () => Results.Ok()).RequireAuthorization(new RequireServiceClientAttribute("capitation-service"));
        await app.StartAsync();

        async Task<HttpStatusCode> Call(string issuer, string key)
        {
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                ChoTokenIssuer.FromKeys(issuer, "cho-api", key, null, TimeSpan.FromMinutes(5)).IssueServiceToken("capitation-service", "acme"));
            return (await client.GetAsync("/svc")).StatusCode;
        }

        (await Call("cho-token-service-svc", tokenServiceKey)).Should().Be(HttpStatusCode.OK);
        (await Call("cho-internal", sharedKey)).Should().Be(HttpStatusCode.Unauthorized);
        (await Call("cho-token-service-svc", sharedKey)).Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static (TokenServiceChoServiceTokenSource, FakeEntra, FakeTokenService, ManualClock) Build(
        int expiresIn = 600,
        string clientIdInResponse = "claims-service",
        string? tenantInResponse = null,
        HttpStatusCode status = HttpStatusCode.OK,
        bool entraFails = false,
        TimeSpan? delay = null)
    {
        var clock = new ManualClock();
        var entra = new FakeEntra { Fails = entraFails };
        var tokenService = new FakeTokenService
        {
            ExpiresIn = expiresIn, ClientId = clientIdInResponse, TenantOverride = tenantInResponse, Status = status, Delay = delay,
        };
        var options = new ChoServiceTokenOptions
        {
            Source = ChoServiceTokenSourceKind.TokenService,
            ClientId = "claims-service",
            TokenServiceUrl = "http://token-service",
            EntraScope = Scope,
        };
        var http = new HttpClient(tokenService) { BaseAddress = new Uri("http://token-service/") };
        return (new TokenServiceChoServiceTokenSource(options, entra, http, null, clock), entra, tokenService, clock);
    }

    private static Dictionary<string, string?> Trust() => new()
    {
        ["ChoAuth:Audience"] = "cho-api",
        ["ChoAuth:Issuers:0:Issuer"] = "cho-token-service-svc",
        ["ChoAuth:Issuers:0:PublicKeyPem"] = PublicPem(PrivatePem()),
        ["ChoAuth:Issuers:0:Kind"] = "Service",
    };

    private static IConfiguration Config(IDictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static string PrivatePem() => ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem();

    private static string PublicPem(string privatePem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privatePem);
        return ec.ExportSubjectPublicKeyInfoPem();
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeEntra : TokenCredential
    {
        private int _issued;
        public bool Fails { get; init; }
        public List<string> Scopes { get; } = new();

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            if (Fails)
                throw new Azure.Identity.CredentialUnavailableException("no workload identity");
            lock (Scopes)
                Scopes.AddRange(requestContext.Scopes);
            return ValueTask.FromResult(new AccessToken(
                "entra-token-" + Interlocked.Increment(ref _issued), DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed record SeenRequest(HttpMethod Method, Uri Uri, string? Authorization, string? TenantId);

    private sealed class FakeTokenService : HttpMessageHandler
    {
        private int _issued;
        public int ExpiresIn { get; init; } = 600;
        public string ClientId { get; init; } = "claims-service";
        public string? TenantOverride { get; init; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public TimeSpan? Delay { get; init; }
        public List<SeenRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var tenant = body.GetProperty("tenantId").GetString();
            lock (Requests)
                Requests.Add(new SeenRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), tenant));
            if (Delay is { } delay)
                await Task.Delay(delay, ct);
            if (Status != HttpStatusCode.OK)
                return new HttpResponseMessage(Status) { Content = JsonContent.Create(new { error = "unknown_service" }) };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new Dictionary<string, object?>
                {
                    ["access_token"] = "svc-token-" + Interlocked.Increment(ref _issued),
                    ["token_type"] = "Bearer",
                    ["expires_in"] = ExpiresIn,
                    ["tenant_id"] = TenantOverride ?? tenant,
                    ["client_id"] = ClientId,
                }),
            };
        }
    }

    private sealed class Capture : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
