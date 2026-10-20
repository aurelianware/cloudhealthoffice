using CloudHealthOffice.Infrastructure.Http;
using CloudHealthOffice.Infrastructure.Security;

namespace ClaimsService.Services.Adjudication.PlanEdits;

public static class PlanEditServiceRegistration
{
    /// <summary>
    /// Binds <see cref="PlanEditServiceOptions"/>, refuses to start when a
    /// tenant's plan edit service is misconfigured, and registers one HTTP
    /// client per configured tenant: its own attempt timeout, one retry inside
    /// twice that budget, its own circuit breaker (one plan's outage never
    /// opens another's), and no redirects.
    /// </summary>
    public static IServiceCollection AddPlanEditService(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(PlanEditServiceOptions.SectionName);
        services.Configure<PlanEditServiceOptions>(section);

        var options = new PlanEditServiceOptions();
        section.Bind(options);

        var allowInsecureHttp = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var problems = options.Validate(new ChoOutboundHosts(configuration, environment), allowInsecureHttp);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The plan edit service configuration is not usable: " + string.Join("; ", problems));
        }

        foreach (var (tenantId, tenant) in options.Tenants)
        {
            var attempt = TimeSpan.FromMilliseconds(tenant.TimeoutMilliseconds);
            services
                .AddHttpClient(PlanEditServiceClient.ClientName(tenantId), client => client.Timeout = attempt)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
                .ConfigureChoResilience(o =>
                {
                    o.AttemptTimeout = attempt;
                    o.TotalTimeout = attempt * 2 + TimeSpan.FromMilliseconds(500);
                    o.MaxRetryAttempts = 1;
                });
        }

        services.AddSingleton<IPlanEditServiceClient, PlanEditServiceClient>();
        return services;
    }
}
