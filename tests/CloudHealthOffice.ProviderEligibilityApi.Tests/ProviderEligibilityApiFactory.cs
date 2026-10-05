using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CloudHealthOffice.ProviderEligibilityApi.Tests;

/// <summary>
/// Runs the API in the Testing environment (the production gateway guard
/// applies there too: only Development is exempt) with development CHO token
/// trust, a substitute eligibility gateway and payer directory. Pass
/// <c>useRealGateway: true</c> to keep the real gateway registrations, and
/// <c>environment: "Production"</c> to boot with asymmetric CHO trust only.
/// </summary>
public sealed class ProviderEligibilityApiFactory : WebApplicationFactory<Program>
{
    // Generated per test run so no credential-shaped literal is committed.
    public static readonly string PracticeKey = Guid.NewGuid().ToString("N");
    public const string PracticeTenant = "third-set-smiles";
    public static readonly string OtherKey = Guid.NewGuid().ToString("N");
    public const string OtherTenant = "other-practice";

    public const string ServiceClientId = "provider-eligibility-api";

    private readonly bool _useRealGateway;
    private readonly string _environment;
    private readonly Dictionary<string, string?> _settings;

    public ProviderEligibilityApiFactory(
        bool useRealGateway = false,
        IDictionary<string, string?>? settings = null,
        string environment = "Testing")
    {
        _useRealGateway = useRealGateway;
        _environment = environment;
        _settings = new Dictionary<string, string?>(
            environment == "Production" ? ProductionChoAuth() : ChoDevelopmentAuth.Configuration(ServiceClientId))
        {
            ["ProviderApi:Clients:0:Name"] = "cdo-third-set-smiles",
            ["ProviderApi:Clients:0:ApiKey"] = PracticeKey,
            ["ProviderApi:Clients:0:TenantId"] = PracticeTenant,
            ["ProviderApi:Clients:1:Name"] = "cdo-other",
            ["ProviderApi:Clients:1:ApiKey"] = OtherKey,
            ["ProviderApi:Clients:1:TenantId"] = OtherTenant,
            ["PayerReference:Sync:Enabled"] = "false",
            ["PayerReference:Sync:OnStartup"] = "false"
        };
        if (settings is not null)
        {
            foreach (var (key, value) in settings) _settings[key] = value;
        }
    }

    public IEligibilityGateway Gateway { get; } = Substitute.For<IEligibilityGateway>();
    public IPayerReferenceService Payers { get; } = Substitute.For<IPayerReferenceService>();

    /// <summary>
    /// Replaces the payer directory synchronizer when set. Used with
    /// <c>PayerReference:Sync:Enabled=true</c> to control readiness.
    /// </summary>
    public IPayerDirectorySynchronizer? Synchronizer { get; init; }
    public ConcurrentQueue<string> LogMessages { get; } = new();

    /// <summary>A provider application: its API key, plus an optional <c>X-Tenant-ID</c> echo.</summary>
    public HttpClient CreateAuthorizedClient(string? key = null, string? tenant = PracticeTenant)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key ?? PracticeKey);
        if (tenant is not null) client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>
    /// A CHO caller: a development token for <paramref name="tenant"/> with
    /// <paramref name="roles"/> (TenantAdmin when none), minted by
    /// <see cref="ChoDevelopmentTokenHandler"/> from the <c>X-Tenant-ID</c> it sends.
    /// </summary>
    public HttpClient CreateChoClient(string tenant = PracticeTenant, params string[] roles)
    {
        var client = CreateDefaultClient(new ChoDevelopmentTokenHandler(roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>Deployed-style trust: one CHO issuer with an RSA public key, nothing symmetric.</summary>
    private static Dictionary<string, string?> ProductionChoAuth()
    {
        using var rsa = RSA.Create(2048);
        return new Dictionary<string, string?>
        {
            ["ChoAuth:Audience"] = "cho-api",
            ["ChoAuth:Issuers:0:Issuer"] = "cho-token-service",
            ["ChoAuth:Issuers:0:PublicKeyPem"] = rsa.ExportSubjectPublicKeyInfoPem()
        };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        // AddChoAuthentication reads ChoAuth while services are registered,
        // before ConfigureAppConfiguration sources apply, so it goes in as host settings.
        foreach (var (key, value) in _settings.Where(s => s.Key.StartsWith("ChoAuth:", StringComparison.Ordinal)))
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(new CapturingLoggerProvider(LogMessages));
            logging.SetMinimumLevel(LogLevel.Debug);
        });

        if (_useRealGateway) return;

        builder.ConfigureServices(services =>
        {
            var resolver = Substitute.For<IHealthcareGatewayResolver>();
            resolver.ResolveCapability<IEligibilityGateway>(Arg.Any<string?>()).Returns(Gateway);
            services.RemoveAll<IHealthcareGatewayResolver>();
            services.AddSingleton(resolver);
            services.RemoveAll<IPayerReferenceService>();
            services.AddSingleton(Payers);
            if (Synchronizer is not null)
            {
                services.RemoveAll<IPayerDirectorySynchronizer>();
                services.AddSingleton(Synchronizer);
            }
        });
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages;

        public CapturingLoggerProvider(ConcurrentQueue<string> messages) => _messages = messages;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _messages;

            public CapturingLogger(ConcurrentQueue<string> messages) => _messages = messages;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _messages.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
            }
        }
    }
}
