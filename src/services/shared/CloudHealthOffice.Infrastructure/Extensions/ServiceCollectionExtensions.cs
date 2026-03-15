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
/// Central extension methods to wire all Cloud Health Office infrastructure into a service.
/// Replaces duplicated setup code across 20+ microservices.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all shared Cloud Health Office infrastructure services: health checks, HTTP context accessor,
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

        // CORS — use custom configuration if provided, otherwise default to AllowAll
        if (options.ConfigureCors is not null)
        {
            services.AddCors(options.ConfigureCors);
        }
        else
        {
            services.AddCors(cors =>
            {
                cors.AddPolicy("AllowAll", policy =>
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader();
                });
            });
        }

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

        // Store the CORS policy name for UseChoInfrastructure
        services.AddSingleton(new CorsPolicyNameHolder(options.CorsPolicyName));

        return services;
    }

    /// <summary>
    /// Configures the Cloud Health Office middleware pipeline: exception handling, tenant middleware,
    /// CORS, health check endpoints, and Swagger (in dev).
    /// <para>
    /// <b>Important:</b> This method does NOT call <c>UseAuthentication()</c>. If your service uses
    /// JWT/Azure AD authentication, you must register authentication middleware yourself before
    /// calling this method so that <c>HttpContext.User</c> is populated for tenant claim extraction.
    /// </para>
    /// <example>
    /// <code>
    /// app.UseAuthentication();           // your auth setup
    /// app.UseChoInfrastructure(config);  // infrastructure pipeline
    /// app.MapControllers();
    /// </code>
    /// </example>
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

        var corsPolicyName = app.ApplicationServices.GetRequiredService<CorsPolicyNameHolder>().PolicyName;
        app.UseCors(corsPolicyName);
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

    /// <summary>
    /// Custom CORS configuration. When set, replaces the default AllowAll policy.
    /// Leave null to use the default permissive policy (AllowAnyOrigin/Method/Header).
    /// </summary>
    public Action<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>? ConfigureCors { get; set; }

    /// <summary>
    /// The CORS policy name applied in the middleware pipeline. Default: "AllowAll".
    /// Must match a policy name registered via <see cref="ConfigureCors"/> if customized.
    /// </summary>
    public string CorsPolicyName { get; set; } = "AllowAll";
}

internal class CorsPolicyNameHolder
{
    public string PolicyName { get; }
    public CorsPolicyNameHolder(string policyName) => PolicyName = policyName;
}
