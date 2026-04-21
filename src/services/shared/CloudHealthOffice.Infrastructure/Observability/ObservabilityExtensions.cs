using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CloudHealthOffice.Infrastructure.Observability;

/// <summary>
/// Extensions to wire OpenTelemetry tracing + metrics into any CHO service.
/// Requires a two-step setup in Program.cs:
///
/// <code>
///   // 1. Register services
///   builder.Services.AddChoObservability(builder.Configuration);
///
///   var app = builder.Build();
///
///   // 2. Map Prometheus /metrics endpoint
///   app.UseChoObservability();
/// </code>
///
/// Configuration (appsettings.json / env vars):
/// <code>
/// {
///   "Observability": {
///     "ServiceName":   "benefit-plan-service",   // defaults to entry assembly name
///     "ServiceVersion": "1.0.0",                 // defaults to assembly version
///     "Environment":   "Development",            // defaults to ASPNETCORE_ENVIRONMENT
///     "OtlpEndpoint":  "http://localhost:4317",  // OTLP gRPC endpoint (null = disabled)
///     "EnableConsole": true                       // console exporter for dev (default false)
///   }
/// }
/// </code>
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Registers OpenTelemetry tracing and metrics with ASP.NET Core, HttpClient,
    /// MongoDB, and Redis instrumentation plus CHO custom sources.
    /// </summary>
    public static IServiceCollection AddChoObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Observability");

        var serviceName = section["ServiceName"]
            ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
            ?? "cho-unknown-service";

        var serviceVersion = section["ServiceVersion"]
            ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "0.0.0";

        var environment = section["Environment"]
            ?? configuration["ASPNETCORE_ENVIRONMENT"]
            ?? "Production";

        var otlpEndpoint = section["OtlpEndpoint"];
        var enableConsole = section.GetValue<bool>("EnableConsole");

        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion)
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = environment,
                ["service.namespace"] = "CloudHealthOffice",
            });

        // ── Tracing ──
        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resourceBuilder)
                    .AddSource(ChoActivitySource.Name)
                    .AddAspNetCoreInstrumentation(opts =>
                    {
                        // Skip health-check noise
                        opts.Filter = ctx =>
                            !ctx.Request.Path.StartsWithSegments("/health") &&
                            !ctx.Request.Path.StartsWithSegments("/ready") &&
                            !ctx.Request.Path.StartsWithSegments("/live") &&
                            !ctx.Request.Path.StartsWithSegments("/metrics");
                    })
                    .AddHttpClientInstrumentation()
                    .AddRedisInstrumentation()
                    .AddSource("MongoDB.Driver.Core.Extensions.DiagnosticSources")
                    .AddSource("Azure.Messaging.ServiceBus.*")
                    .AddProcessor(new PhiScrubbingSpanProcessor(serviceName));

                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = new Uri(otlpEndpoint);
                    });
                }

                if (enableConsole)
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resourceBuilder)
                    .AddMeter(ChoMetrics.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = new Uri(otlpEndpoint);
                    });
                }

                // Always expose /metrics for Prometheus scraping
                metrics.AddPrometheusExporter();

                if (enableConsole)
                {
                    metrics.AddConsoleExporter();
                }
            });

        return services;
    }

    /// <summary>
    /// Maps the Prometheus scraping endpoint at /metrics.
    /// Call after UseRouting() in the middleware pipeline.
    /// </summary>
    public static IApplicationBuilder UseChoObservability(
        this IApplicationBuilder app)
    {
        // Prometheus scraping endpoint
        app.UseOpenTelemetryPrometheusScrapingEndpoint();
        return app;
    }
}
