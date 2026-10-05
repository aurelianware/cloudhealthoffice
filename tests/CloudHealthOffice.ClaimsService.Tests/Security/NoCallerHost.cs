using System.Net;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CloudHealthOffice.ClaimsService.Tests.Security;

/// <summary>
/// The service's outbound HTTP stack as production wires it
/// (<c>AddChoAuthentication</c> registers <c>ChoOutboundTokenHandler</c> on every
/// factory client) with no inbound request: what a message consumer, hosted
/// service or background job sees. The primary handler captures what would go
/// on the wire.
/// </summary>
internal sealed class NoCallerHost : IDisposable
{
    public NoCallerHost(
        string serviceClientId,
        Action<IServiceCollection, CapturingHandler> register,
        IDictionary<string, string?>? configuration = null)
    {
        var settings = new Dictionary<string, string?>(ChoDevelopmentAuth.Configuration(serviceClientId));
        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
            settings[key] = value;
        // The service's own appsettings.json underneath, so its outbound host
        // allowlist (ChoAuth:Outbound) is the one production uses.
        Configuration = new ConfigurationBuilder()
            .AddJsonFile(ServiceAppSettings("claims-service"), optional: false)
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Configuration);
        services.AddChoAuthentication(Configuration, new DevelopmentEnvironment());
        register(services, Outbound);
        Services = services.BuildServiceProvider();
    }

    public IConfiguration Configuration { get; }

    /// <summary>src/services/{service}/appsettings.json, found above the test output directory.</summary>
    private static string ServiceAppSettings(string service)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "services", service, "appsettings.json");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"{service} appsettings.json not found above {AppContext.BaseDirectory}");
    }

    public CapturingHandler Outbound { get; } = new();

    public ServiceProvider Services { get; }

    public void Dispose() => Services.Dispose();

    /// <summary>The tenant and subject of the bearer token on <paramref name="request"/>, or nulls.</summary>
    public static (string? Tenant, string? Subject) TokenOf(HttpRequestMessage? request)
    {
        var auth = request?.Headers.Authorization;
        if (auth is null || !string.Equals(auth.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                         || string.IsNullOrEmpty(auth.Parameter))
            return (null, null);

        var token = new JsonWebToken(auth.Parameter);
        var tenant = token.Claims.FirstOrDefault(c => c.Type == ChoClaimTypes.TenantId)?.Value;
        return (tenant, token.Subject);
    }

    internal sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = new();

        /// <summary>Answer for every request; 200 with <c>{}</c> by default.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

        public IReadOnlyList<HttpRequestMessage> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        public HttpRequestMessage? Last => Requests.LastOrDefault();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
