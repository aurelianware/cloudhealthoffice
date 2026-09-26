using System.Collections.Concurrent;
using System.Net.Http.Headers;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CloudHealthOffice.ProviderEligibilityApi.Tests;

/// <summary>
/// Runs the API in the Production environment (so production startup guards
/// apply) with a substitute eligibility gateway and payer directory. Pass
/// <c>useRealGateway: true</c> to keep the real gateway registrations.
/// </summary>
public sealed class ProviderEligibilityApiFactory : WebApplicationFactory<Program>
{
    // Generated per test run so no credential-shaped literal is committed.
    public static readonly string PracticeKey = Guid.NewGuid().ToString("N");
    public const string PracticeTenant = "third-set-smiles";
    public static readonly string OtherKey = Guid.NewGuid().ToString("N");
    public const string OtherTenant = "other-practice";

    private readonly bool _useRealGateway;
    private readonly Dictionary<string, string?> _settings;

    public ProviderEligibilityApiFactory(
        bool useRealGateway = false,
        IDictionary<string, string?>? settings = null)
    {
        _useRealGateway = useRealGateway;
        _settings = new Dictionary<string, string?>
        {
            ["ProviderApi:Clients:0:Name"] = "cdo-third-set-smiles",
            ["ProviderApi:Clients:0:ApiKey"] = PracticeKey,
            ["ProviderApi:Clients:0:Tenants:0"] = PracticeTenant,
            ["ProviderApi:Clients:1:Name"] = "cdo-other",
            ["ProviderApi:Clients:1:ApiKey"] = OtherKey,
            ["ProviderApi:Clients:1:Tenants:0"] = OtherTenant,
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

    public HttpClient CreateAuthorizedClient(string? key = null, string? tenant = PracticeTenant)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key ?? PracticeKey);
        if (tenant is not null) client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
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
