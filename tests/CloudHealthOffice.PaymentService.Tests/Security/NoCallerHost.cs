using System.Net;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CloudHealthOffice.PaymentService.Tests.Security;

/// <summary>
/// The service's outbound HTTP stack as production wires it
/// (<c>AddChoAuthentication</c> registers <c>ChoOutboundTokenHandler</c> on every
/// factory client) with no inbound request: what a message consumer, hosted
/// service or scheduled job sees. The primary handler captures what would go on
/// the wire. (Same shape as premium-billing-service's NoCallerHost.)
/// </summary>
public sealed class NoCallerHost : IDisposable
{
    public NoCallerHost(
        string serviceClientId,
        Action<IServiceCollection, CapturingHandler> register,
        IDictionary<string, string?>? configuration = null)
    {
        var settings = new Dictionary<string, string?>(ChoDevelopmentAuth.Configuration(serviceClientId));
        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
            settings[key] = value;
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Configuration);
        services.AddChoAuthentication(Configuration, new DevelopmentEnvironment());
        register(services, Outbound);
        Services = services.BuildServiceProvider();
    }

    public IConfiguration Configuration { get; }

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

    public sealed class CapturingHandler : HttpMessageHandler
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

        public void Clear()
        {
            lock (_requests) _requests.Clear();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);
            return Task.FromResult(Respond(request));
        }

        // Shared by a WebApplicationFactory across tests; the client factory's
        // handler rotation must not dispose it.
        protected override void Dispose(bool disposing) { }
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
