using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace CloudHealthOffice.Infrastructure.Tests;

/// <summary>
/// WebApplicationFactory variant used by <see cref="ObservabilityTestHelper"/>.
///
/// Two test-host modifications:
///
/// 1. Strips every app-defined <see cref="IHostedService"/> so smoke tests
///    don't wake up production workers — OpenIddict seed loaders, Kafka
///    consumers, SLA watchdogs, Mongo index initializers, Service Bus mirror
///    reconcilers — many of which throw during <c>StartAsync</c> when the
///    external dependency they need isn't reachable from a CI runner.
///    OpenTelemetry's own hosted service (namespace-based filter) is kept so
///    the MeterProvider boots and <c>/metrics</c> stays scrapable.
///
/// 2. If the app registers an <see cref="IConnectionMultiplexer"/>, replaces
///    its factory with a non-connecting Moq stub. Background: AddChoObservability
///    wires OpenTelemetry's Redis instrumentation, and OTel eagerly resolves
///    IConnectionMultiplexer from DI when the TracerProvider is built during
///    the OTel hosted service StartAsync. In production that resolution hits
///    a real multiplexer; in tests it triggers StackExchange.Redis.Connect,
///    which throws when no Redis is reachable. The stub satisfies the DI
///    resolution — OTel only needs a non-null reference at build time, it
///    doesn't exercise the multiplexer during the smoke test.
/// </summary>
public class ObservabilityTestFactory<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var hostedToRemove = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .Where(d =>
                {
                    var implType = d.ImplementationType
                        ?? d.ImplementationInstance?.GetType();
                    // Factory-based registrations (ImplementationFactory only)
                    // have no statically knowable type — treat those as
                    // app-defined and strip them. OpenTelemetry registers its
                    // TelemetryHostedService via AddHostedService<T>, so its
                    // ImplementationType resolves and this check preserves it.
                    var ns = implType?.Namespace ?? string.Empty;
                    return !ns.StartsWith("OpenTelemetry", StringComparison.Ordinal);
                })
                .ToList();

            foreach (var descriptor in hostedToRemove)
            {
                services.Remove(descriptor);
            }

            var multiplexer = services.FirstOrDefault(
                d => d.ServiceType == typeof(IConnectionMultiplexer));
            if (multiplexer is not null)
            {
                services.Remove(multiplexer);
                services.AddSingleton<IConnectionMultiplexer>(_ =>
                    new Mock<IConnectionMultiplexer>().Object);
            }
        });
    }
}
