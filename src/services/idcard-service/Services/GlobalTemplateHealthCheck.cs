using IdCardService.Repositories;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IdCardService.Services;

/// <summary>
/// Fails fast at deployment time if a tenant is missing the global default
/// ID card template. Template resolution falls through to global-default as
/// its last step; missing it turns every order into a runtime failure.
/// Configuration: <c>IdCard:HealthCheckTenants</c> — list of tenant ids that
/// must have a global template. When empty, the check passes.
/// </summary>
public class GlobalTemplateHealthCheck : IHealthCheck
{
    private readonly IIdCardTemplateRepository _templates;
    private readonly IConfiguration _configuration;

    public GlobalTemplateHealthCheck(IIdCardTemplateRepository templates, IConfiguration configuration)
    {
        _templates = templates;
        _configuration = configuration;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var tenants = _configuration.GetSection("IdCard:HealthCheckTenants").Get<string[]>() ?? Array.Empty<string>();
        if (tenants.Length == 0)
        {
            return HealthCheckResult.Healthy("No tenants configured for global-template check");
        }

        var missing = new List<string>();
        foreach (var tenantId in tenants)
        {
            var template = await _templates.FindGlobalDefaultAsync(tenantId, cancellationToken);
            if (template == null)
            {
                missing.Add(tenantId);
            }
        }

        return missing.Count == 0
            ? HealthCheckResult.Healthy($"All {tenants.Length} tenant(s) have a global default template")
            : HealthCheckResult.Unhealthy($"Tenants missing global ID card template: {string.Join(", ", missing)}");
    }
}
