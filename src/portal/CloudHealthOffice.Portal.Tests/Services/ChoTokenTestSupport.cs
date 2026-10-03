using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Hosting;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>Shared fixtures for tests of the CHO token flow.</summary>
internal static class ChoTokenTestSupport
{
    public const string TokenServiceUrl = "http://token-service.test";
    public const string Scope = "api://cho-test/Cho.Token";

    public static ClaimsPrincipal EntraUser(string tid = "entra-tid-1", string oid = "oid-1", string email = "jane@acme.com")
        => new(new ClaimsIdentity(new[]
        {
            new Claim("tid", tid),
            new Claim("oid", oid),
            new Claim(ClaimTypes.Email, email),
            new Claim("name", "Jane Doe"),
        }, "TestAuth"));

    public static ClaimsPrincipal LocalDemoUser()
        => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "local-demo-admin"),
            new Claim(ClaimTypes.Name, "Local Demo Admin"),
            new Claim(ClaimTypes.Email, "local-demo-user"),
            new Claim("oid", "local-demo-admin"),
            new Claim("tid", "local-demo"),
            new Claim("cho_local_demo", "true"),
        }, "Cookies"));

    public static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    public static string ExchangeJson(
        string tenantId = "tenant-1",
        string accessToken = "cho-token-1",
        int expiresIn = 3600,
        string[]? roles = null,
        string[]? permissions = null,
        string tenantName = "Acme Health",
        string userId = "usr-1",
        string email = "jane@acme.com",
        string displayName = "Jane Doe",
        string firstName = "Jane",
        string lastName = "Doe",
        string department = "Claims")
        => JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = expiresIn,
            tenant_id = tenantId,
            tenant_name = tenantName,
            roles = roles ?? new[] { "ClaimsExaminer" },
            permissions = permissions ?? new[] { "claims:read", "claims:work" },
            user = new { id = userId, email, displayName, firstName, lastName, department },
        });

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string? RequestedTenant(HttpRequestMessage request)
    {
        if (request.Content == null) return null;
        var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("tenantId", out var t) ? t.GetString() : null;
    }
}

internal sealed class StaticAuthenticationStateProvider : AuthenticationStateProvider
{
    public StaticAuthenticationStateProvider(ClaimsPrincipal user) => User = user;

    public ClaimsPrincipal User { get; set; }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(new AuthenticationState(User));
}

internal sealed class SingleHandlerHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public SingleHandlerHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
    public List<string> RequestedNames { get; } = new();

    public HttpClient CreateClient(string name)
    {
        RequestedNames.Add(name);
        return new HttpClient(_handler, disposeHandler: false);
    }
}

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public TestHostEnvironment(string environmentName) => EnvironmentName = environmentName;
    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "CloudHealthOffice.Portal.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}
