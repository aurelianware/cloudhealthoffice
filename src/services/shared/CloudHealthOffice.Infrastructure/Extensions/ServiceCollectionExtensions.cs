using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Extensions;

/// <summary>
/// Central extension methods to wire all CHO infrastructure into a service.
/// Replaces duplicated setup code across 20+ microservices.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all shared CHO infrastructure services: health checks, HTTP context accessor,
    /// CORS, Swagger, and database connections (MongoDB or Cosmos DB based on configuration).
    /// </summary>
    public static IServiceCollection AddChoInfrastructure(this IServiceCollection services, IConfiguration configuration, Action<ChoInfrastructureOptions>? configure = null)
    {
        var options = new ChoInfrastructureOptions();
        configure?.Invoke(options);

        services.AddHttpContextAccessor();
        services.AddControllers();
        services.AddEndpointsApiExplorer();

        // Health checks
        services.AddChoHealthChecks(hc =>
        {
            hc.MongoDbConnectionString = configuration["MongoDb:ConnectionString"];
            hc.RedisConnectionString = configuration["Redis:ConnectionString"];
        });

        // CORS
        services.AddCors(cors =>
        {
            cors.AddPolicy("AllowAll", policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        // Swagger
        if (!string.IsNullOrEmpty(options.ServiceName))
        {
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = $"{options.ServiceName} API",
                    Version = "v1",
                    Description = options.ServiceDescription
                });
            });
        }
        else
        {
            services.AddSwaggerGen();
        }

        // Database registration
        RegisterDatabase(services, configuration);

        // Store tenant middleware options for use in UseChoInfrastructure
        services.AddSingleton(options.TenantOptions);

        return services;
    }

    /// <summary>
    /// Configures the CHO middleware pipeline: exception handling, tenant middleware,
    /// CORS, health check endpoints, Swagger (in dev), and authorization.
    /// </summary>
    public static IApplicationBuilder UseChoInfrastructure(this IApplicationBuilder app, IConfiguration configuration)
    {
        var env = app.ApplicationServices.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>();

        // Exception handling (outermost — catches everything)
        app.UseMiddleware<ExceptionHandlingMiddleware>();

        if (env.EnvironmentName == "Development")
        {
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "API v1");
                c.RoutePrefix = string.Empty;
            });
        }

        app.UseHttpsRedirection();

        // Tenant middleware
        var tenantOptions = app.ApplicationServices.GetRequiredService<TenantMiddlewareOptions>();
        app.UseMiddleware<TenantMiddleware>(tenantOptions);

        app.UseCors("AllowAll");
        app.UseAuthorization();

        // Health check endpoints
        app.MapChoHealthChecks();

        return app;
    }

    private static void RegisterDatabase(IServiceCollection services, IConfiguration configuration)
    {
        var mongoConnectionString = configuration["MongoDb:ConnectionString"];

        if (!string.IsNullOrEmpty(mongoConnectionString))
        {
            services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnectionString));
            services.AddScoped<MongoDbConnectionFactory>();
            services.AddScoped<IMongoDatabase>(sp => sp.GetRequiredService<MongoDbConnectionFactory>().GetDatabase());
        }
        else
        {
            var endpoint = configuration["CosmosDb:Endpoint"];
            var key = configuration["CosmosDb:Key"];
            var connectionString = configuration["CosmosDb:ConnectionString"];

            if (!string.IsNullOrEmpty(connectionString))
            {
                services.AddSingleton<CosmosClient>(_ =>
                    new CosmosClient(connectionString, new CosmosClientOptions
                    {
                        Serializer = new CosmosSystemTextJsonSerializer()
                    }));
            }
            else if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(key))
            {
                services.AddSingleton<CosmosClient>(_ =>
                    new CosmosClient(endpoint, key, new CosmosClientOptions
                    {
                        Serializer = new CosmosSystemTextJsonSerializer()
                    }));
            }
        }
    }
}

public class ChoInfrastructureOptions
{
    /// <summary>Service display name for Swagger (e.g., "Claims Service").</summary>
    public string? ServiceName { get; set; }

    /// <summary>Service description for Swagger.</summary>
    public string? ServiceDescription { get; set; }

    /// <summary>Tenant middleware configuration.</summary>
    public TenantMiddlewareOptions TenantOptions { get; set; } = new();
}
