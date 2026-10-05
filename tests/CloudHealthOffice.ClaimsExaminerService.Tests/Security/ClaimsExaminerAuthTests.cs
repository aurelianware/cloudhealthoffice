using System.Net;
using System.Net.Http.Json;
using ClaimsExaminerService.Models;
using ClaimsExaminerService.Services;
using ClaimsExaminerService.Services.Anthropic;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit;

namespace CloudHealthOffice.ClaimsExaminerService.Tests.Security;

/// <summary>
/// Stands in for a future claims-examiner endpoint. It carries no permission
/// attribute, so the service's defaults (claims:read for GET, claims:work for
/// writes) are what guard it. The service ships no controllers of its own today.
/// </summary>
[ApiController]
[Route("api/__auth-probe")]
public sealed class AuthProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { tenant = HttpContext.Items["TenantId"] });

    [HttpPost]
    public IActionResult Post() => Ok();
}

/// <summary>Records outbound requests instead of sending them.</summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public List<HttpRequestMessage> Requests { get; } = new();

    public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }
}

public sealed class ClaimsExaminerAuthFactory : WebApplicationFactory<Program>
{
    public RecordingHandler ClaimsService { get; } = new(req => req.Method == HttpMethod.Get
        ? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { claimId = "CLM-1", tenantId = "tenant-a" })
        }
        : new HttpResponseMessage(HttpStatusCode.OK));

    public RecordingHandler Anthropic { get; } = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { model = "m", content = Array.Empty<object>() })
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            // The Kafka consumer is not under test and needs no broker here.
            foreach (var hosted in services
                         .Where(d => d.ServiceType == typeof(IHostedService)
                                     && d.ImplementationType?.Namespace?.StartsWith("ClaimsExaminerService") == true)
                         .ToList())
            {
                services.Remove(hosted);
            }

            services.AddControllers().AddApplicationPart(typeof(AuthProbeController).Assembly);

            services.Configure<HttpClientFactoryOptions>(nameof(IClaimsServiceClient),
                o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = ClaimsService));
            services.Configure<HttpClientFactoryOptions>(nameof(IAnthropicClient),
                o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = Anthropic));
        });
    }
}

/// <summary>
/// The real claims-examiner pipeline and its outbound clients.
/// </summary>
public class ClaimsExaminerAuthTests : IClassFixture<ClaimsExaminerAuthFactory>
{
    private const string Probe = "/api/__auth-probe";
    private readonly ClaimsExaminerAuthFactory _factory;

    public ClaimsExaminerAuthTests(ClaimsExaminerAuthFactory factory) => _factory = factory;

    // ── Inbound ──────────────────────────────────────────────────────

    [Fact]
    public async Task HealthProbe_NeedsNoToken()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsUnauthorized()
    {
        var response = await _factory.CreateClient().GetAsync(Probe);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-a");

        var response = await client.GetAsync(Probe);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", ChoDevelopmentAuth.UserToken("tenant-a", ChoRolePermissions.ClaimsExaminer));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-b");

        var response = await client.GetAsync(Probe);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ClaimsExaminer_IsServed_InTheTokenTenant()
    {
        var client = _factory.CreateDefaultClient(
            new ChoDevelopmentTokenHandler("examiner-1", ChoRolePermissions.ClaimsExaminer));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-a");

        var get = await client.GetAsync(Probe);
        var post = await client.PostAsync(Probe, null);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Contains("tenant-a", await get.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
    }

    [Fact]
    public async Task RoleWithoutClaimsRead_IsForbidden()
    {
        var client = _factory.CreateDefaultClient(
            new ChoDevelopmentTokenHandler("dev-user", ChoRolePermissions.EnrollmentSpecialist));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-a");

        var response = await client.GetAsync(Probe);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyClaimsRole_CannotWrite()
    {
        // MemberServices holds claims:read but not claims:work.
        var client = _factory.CreateDefaultClient(
            new ChoDevelopmentTokenHandler("dev-user", ChoRolePermissions.MemberServices));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-a");

        var get = await client.GetAsync(Probe);
        var post = await client.PostAsync(Probe, null);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    // ── Outbound (Kafka consumer path: no inbound caller) ────────────

    [Fact]
    public async Task ClaimsServiceCalls_CarryAServiceToken_ForTheEventTenant()
    {
        _factory.ClaimsService.Requests.Clear();
        using var scope = _factory.Services.CreateScope();
        var claims = scope.ServiceProvider.GetRequiredService<IClaimsServiceClient>();

        await claims.GetClaimAsync("CLM-1", "tenant-a", CancellationToken.None);
        await claims.SetAiExaminationAsync("CLM-1", "tenant-a", new AiExaminationDto(), CancellationToken.None);

        Assert.Equal(2, _factory.ClaimsService.Requests.Count);
        foreach (var request in _factory.ClaimsService.Requests)
        {
            Assert.NotNull(request.Headers.Authorization);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var token = new JsonWebToken(request.Headers.Authorization.Parameter);
            Assert.Equal("tenant-a", token.GetClaim(ChoClaimTypes.TenantId).Value);
            Assert.Equal(ChoDevelopmentAuth.ServiceIssuer, token.Issuer);
            Assert.Contains(token.Claims, c => c.Type == ChoClaimTypes.Role && c.Value == ChoServiceRole.Name);
        }
    }

    [Fact]
    public async Task AnthropicCalls_NeverCarryAChoToken()
    {
        _factory.Anthropic.Requests.Clear();
        using var scope = _factory.Services.CreateScope();
        var anthropic = scope.ServiceProvider.GetRequiredService<IAnthropicClient>();

        try
        {
            await anthropic.CallWithToolAsync("system", "user",
                new AnthropicTool("t", "d", new System.Text.Json.Nodes.JsonObject()),
                CancellationToken.None);
        }
        catch
        {
            // The canned response is not a valid tool result; only the request matters.
        }

        var request = Assert.Single(_factory.Anthropic.Requests);
        Assert.Null(request.Headers.Authorization);
    }
}
