using CloudHealthOffice.GrouperEngine.Services;

namespace CloudHealthOffice.PricingApi.Services;

/// <summary>
/// Registers the Pricing API's DRG resolver over the grouper the engine binds.
///
/// <para>
/// The grouper itself, and the options that select a CMS release, moved to
/// <see cref="CloudHealthOffice.GrouperEngine.Services.MsDrgGrouperRegistration"/>
/// when claims-service began grouping too: both hosts read the same
/// <c>MsDrgGrouper</c> configuration section and get the same grouper, so the
/// Pricing API and adjudication cannot disagree about what a stay groups to.
/// The <c>appsettings</c> shape is unchanged.
/// </para>
/// </summary>
public static class PricingApiGrouperRegistration
{
    /// <summary>
    /// Registers <see cref="IInpatientDrgResolver"/> and, through the engine,
    /// the configured <see cref="IMsDrgGrouper"/>.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">Configuration holding <c>MsDrgGrouper</c>.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddInpatientDrgResolver(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IInpatientDrgResolver, GrouperInpatientDrgResolver>();
        return services.AddMsDrgGrouper(configuration);
    }
}
