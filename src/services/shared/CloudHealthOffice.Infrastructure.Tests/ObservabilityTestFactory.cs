using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.Infrastructure.Tests;

/// <summary>
/// WebApplicationFactory variant used by <see cref="ObservabilityTestHelper"/>.
/// Strips every app-defined <see cref="IHostedService"/> before the test host
/// starts so smoke tests don't get bogged down in production workers —
/// OpenIddict seed loaders, Kafka consumers, SLA watchdogs, Mongo index
/// initializers, Service Bus mirror reconcilers, etc. — many of which throw
/// during <c>StartAsync</c> when the external dependency they need isn't
/// reachable from the CI runner.
///
/// OpenTelemetry's own hosted service is preserved so the MeterProvider boots
/// and <c>/metrics</c> stays scrapable — that is exactly what these smoke
/// tests assert on.
/// </summary>
public class ObservabilityTestFactory<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var toRemove = services
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

            foreach (var descriptor in toRemove)
            {
                services.Remove(descriptor);
            }
        });
    }
}
