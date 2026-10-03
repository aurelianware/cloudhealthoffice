using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.TokenService.Directory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.TokenService.Tests;

public static class Ids
{
    public const string ApiClientId = "cfada1ac-f251-48ea-9330-39212aa4c862";
    public const string ApiAppIdUri = "api://cfada1ac-f251-48ea-9330-39212aa4c862";

    public const string AcmeDirectory = "11111111-1111-1111-1111-111111111111";
    public const string BetaDirectory = "22222222-2222-2222-2222-222222222222";
    public const string GuestDirectory = "33333333-3333-3333-3333-333333333333";
    public const string PlatformDirectory = "99999999-9999-9999-9999-999999999999";
}

/// <summary>
/// Test-only Entra: a local RSA key the token-service is configured to trust
/// in tests, and a token factory shaped like Entra v2 access tokens.
/// </summary>
public static class TestEntra
{
    public static readonly RsaSecurityKey Key = CreateKey();

    private static RsaSecurityKey CreateKey()
    {
        var key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-entra-key" };
        return key;
    }

    public static string Token(
        string tid,
        string oid,
        string? scp = "Cho.Token",
        string aud = Ids.ApiClientId,
        string? username = null,
        string[]? roles = null,
        string? idtyp = null,
        string? issuer = null,
        DateTime? expires = null,
        string? name = "Test User")
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["tid"] = tid,
            ["oid"] = oid,
            ["sub"] = "pairwise-" + oid,
            ["ver"] = "2.0",
        };
        if (scp != null) claims["scp"] = scp;
        if (username != null) claims["preferred_username"] = username;
        if (roles != null) claims["roles"] = roles;
        if (idtyp != null) claims["idtyp"] = idtyp;
        if (name != null) claims["name"] = name;

        var exp = expires ?? now.AddMinutes(30);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? $"https://login.microsoftonline.com/{tid}/v2.0",
            Audience = aud,
            IssuedAt = exp.AddMinutes(-60),
            NotBefore = exp.AddMinutes(-60),
            Expires = exp,
            Claims = claims,
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        });
    }
}

/// <summary>
/// In-memory tenant-service identity endpoints. Membership lookups return
/// every record with the oid, whatever its recorded directory, so the
/// token-service's own directory rule is what the tests exercise (the real
/// tenant-service also pre-filters by directory).
/// </summary>
public sealed class FakeTenantService : HttpMessageHandler
{
    public List<DirectoryTenant> Tenants { get; } = new();
    public List<DirectoryUser> Users { get; } = new();
    public List<(string TenantId, string UserId, string Oid, string Tid)> Links { get; } = new();
    public List<string?> AuthorizationHeaders { get; } = new();
    public bool Down { get; set; }

    /// <summary>Bodies of internal redemption calls.</summary>
    public List<JsonNode> Redemptions { get; } = new();

    /// <summary>tenant-service's answer to a redemption; by default every code is unknown.</summary>
    public Func<JsonNode, HttpResponseMessage> Redeem { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { error = "not_found" }) };

    public DirectoryTenant AddTenant(string tenantId, string? azureTenantId, bool active = true)
    {
        var t = new DirectoryTenant
        {
            TenantId = tenantId,
            TenantName = tenantId.ToUpperInvariant() + " Health",
            AzureTenantId = azureTenantId,
            Status = active ? "active" : "suspended",
            IsActive = active,
        };
        Tenants.Add(t);
        return t;
    }

    public DirectoryUser AddUser(string tenantId, string email, string? oid = null, string? tid = null,
        string status = "Active", params string[] roles)
    {
        var u = new DirectoryUser
        {
            Id = "user-" + Guid.NewGuid().ToString("N")[..8],
            TenantId = tenantId,
            Email = email,
            DisplayName = "Pat Example",
            FirstName = "Pat",
            LastName = "Example",
            Department = "Claims",
            Roles = roles.ToList(),
            Status = status,
            AzureAdObjectId = oid ?? string.Empty,
            AzureAdTenantId = tid ?? string.Empty,
        };
        Users.Add(u);
        return u;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
        if (Down)
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        const string b = "/internal/v1/identity/";
        if (!path.StartsWith(b, StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        var parts = path[b.Length..].Split('/').Select(Uri.UnescapeDataString).ToArray();

        if (request.Method == HttpMethod.Post && parts is ["invitations", "redeem"])
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Redemptions.Add(body);
            return Redeem(body);
        }

        if (request.Method == HttpMethod.Get && parts is ["memberships"])
        {
            var oid = query["oid"];
            var list = Users.Where(u => u.AzureAdObjectId == oid)
                .Select(u => new DirectoryMembership { User = u, Tenant = Tenants.FirstOrDefault(t => t.TenantId == u.TenantId) })
                .ToList();
            return Json(list);
        }

        if (request.Method == HttpMethod.Get && parts is ["tenants"])
        {
            var az = query["azureTenantId"];
            return Json(Tenants.Where(t => string.IsNullOrEmpty(az) || t.AzureTenantId == az).ToList());
        }

        if (request.Method == HttpMethod.Get && parts is ["tenants", var tenantId])
        {
            var t = Tenants.FirstOrDefault(x => x.TenantId == tenantId);
            return t == null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(t);
        }

        if (request.Method == HttpMethod.Post && parts is ["tenants", var tid2, "users", "find-by-email"])
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var email = body["email"]!.GetValue<string>();
            var u = Users.FirstOrDefault(x => x.TenantId == tid2 && string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
            return u == null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(u);
        }

        if (request.Method == HttpMethod.Post && parts is ["tenants", var tid3, "users", var userId, "entra-link"])
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var oid = body["azureAdObjectId"]!.GetValue<string>();
            var dir = body["azureAdTenantId"]!.GetValue<string>();
            var u = Users.FirstOrDefault(x => x.TenantId == tid3 && x.Id == userId);
            if (u == null)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var free = string.IsNullOrEmpty(u.AzureAdObjectId)
                       || (u.AzureAdObjectId == oid && (string.IsNullOrEmpty(u.AzureAdTenantId) || u.AzureAdTenantId == dir));
            if (!free)
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            u.AzureAdObjectId = oid;
            u.AzureAdTenantId = dir;
            Links.Add((tid3, userId, oid, dir));
            return Json(u);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object value)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, value.GetType()) };
}

public sealed class TokenServiceFactory : WebApplicationFactory<Program>
{
    public FakeTenantService TenantService { get; } = new();

    /// <summary>Every log line the host writes.</summary>
    public CapturedLogs Logs { get; } = new();

    /// <summary>TokenService:InvitationRedeemPermitsPerMinute for this host.</summary>
    public int RedeemPermitsPerMinute { get; init; } = 10;

    /// <summary>The CHO token signing key (EC P-256 PEM), generated per factory.</summary>
    public string SigningKeyPem { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem();

    /// <summary>More host settings (for example the WorkloadTokens section).</summary>
    public IReadOnlyDictionary<string, string?> ExtraSettings { get; init; } = new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in ExtraSettings)
            builder.UseSetting(key, value);
        builder.UseEnvironment("Testing");
        builder.UseSetting("TokenService:PlatformTenantId", Ids.PlatformDirectory);
        builder.UseSetting("TokenSigning:Issuer", "cho-token-service");
        builder.UseSetting("TokenSigning:PrivateKeyPem", SigningKeyPem);
        builder.UseSetting("ChoAuth:ServiceToken:Issuer", "cho-internal-dev");
        builder.UseSetting("ChoAuth:ServiceToken:ClientId", "token-service");
        builder.UseSetting("ChoAuth:ServiceToken:SymmetricKey", Infrastructure.Security.ChoDevelopmentAuth.SymmetricKey);
        builder.UseSetting("Services:TenantService", "http://tenant-service");
        builder.UseSetting("TokenService:InvitationRedeemPermitsPerMinute", RedeemPermitsPerMinute.ToString());
        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
            logging.AddProvider(Logs);
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(HttpTenantDirectory.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => TenantService);

            // Tests only: trust the local test key instead of Entra's published
            // keys. Issuer, audience, scope and lifetime rules are unchanged.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                var config = new OpenIdConnectConfiguration();
                config.SigningKeys.Add(TestEntra.Key);
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
            });
        });
    }

    public HttpClient ClientWith(string? entraToken)
    {
        var client = CreateClient();
        if (entraToken != null)
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", entraToken);
        return client;
    }
}

public sealed class CapturedLogs : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Category, string Message)> _entries = new();

    public IReadOnlyList<(string Category, string Message)> Entries => _entries.ToArray();

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, System.Collections.Concurrent.ConcurrentQueue<(string, string)> entries)
        : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Enqueue((category, formatter(state, exception) + " " + exception));
    }
}

public static class HttpExtensions
{
    public static Task<HttpResponseMessage> ExchangeAsync(this HttpClient client, string? tenantId)
        => client.PostAsJsonAsync("/v1/token/exchange", new { tenantId });

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
