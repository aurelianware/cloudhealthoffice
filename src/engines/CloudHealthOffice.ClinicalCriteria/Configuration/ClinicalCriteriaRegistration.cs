using CloudHealthOffice.ClinicalCriteria.Abstractions;
using CloudHealthOffice.ClinicalCriteria.Providers;
using CloudHealthOffice.ClinicalCriteria.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ClinicalCriteria.Configuration;

/// <summary>
/// DI registration for the clinical criteria seam.
///
/// <code>
/// builder.Services.AddClinicalCriteria(builder.Configuration);
/// </code>
///
/// Requires <see cref="PriorAuthRuleEngine.Abstractions.IPriorAuthRuleEngine"/>
/// in the container, which the host registers with
/// <c>AddPriorAuthRuleEngine(...)</c>. The licensed providers are registered
/// only when enabled in configuration, so a deployment without a licence
/// carries no inert vendor path.
///
/// A customer with a licence replaces one of them by registering their own
/// <see cref="IClinicalCriteriaProvider"/> with the same
/// <see cref="IClinicalCriteriaProvider.ProviderId"/> after this call: the
/// last registration of an id wins.
/// </summary>
public static class ClinicalCriteriaServiceCollectionExtensions
{
    public static IServiceCollection AddClinicalCriteria(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ClinicalCriteriaOptions>(
            configuration.GetSection(ClinicalCriteriaOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IClinicalCriteriaProvider, LocalRulesCriteriaProvider>();

        // Bound once here, not at resolve time: whether a licensed provider is
        // registered at all is a deployment fact, and a provider appearing
        // mid-process because configuration reloaded would make the status
        // endpoint lie about what this deployment can do.
        var bound = configuration.GetSection(ClinicalCriteriaOptions.SectionName)
            .Get<ClinicalCriteriaOptions>() ?? new ClinicalCriteriaOptions();

        if (bound.InterQual.Enabled)
            services.AddScoped<IClinicalCriteriaProvider>(sp =>
                new InterQualCriteriaProvider(
                    sp.GetRequiredService<IOptions<ClinicalCriteriaOptions>>().Value.InterQual));

        if (bound.Mcg.Enabled)
            services.AddScoped<IClinicalCriteriaProvider>(sp =>
                new McgCriteriaProvider(
                    sp.GetRequiredService<IOptions<ClinicalCriteriaOptions>>().Value.Mcg));

        services.AddScoped<IClinicalCriteriaService, ClinicalCriteriaService>();

        return services;
    }
}
