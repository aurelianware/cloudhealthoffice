using CloudHealthOffice.GrouperEngine.Mask;
using CloudHealthOffice.GrouperEngine.Services;

namespace CloudHealthOffice.PricingApi.Services;

/// <summary>
/// One CMS MS-DRG release a deployment holds a licence for, and the discharge
/// dates it prices.
/// </summary>
public sealed class MsDrgReleaseOptions
{
    /// <summary>
    /// Directory holding the release's JSON tables, as distributed by CMS.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>The release's version label, e.g. <c>V440</c>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>First discharge date this release prices, normally 1 October.</summary>
    public DateOnly FirstDischargeDate { get; set; }

    /// <summary>Last discharge date it prices; leave unset while it is current.</summary>
    public DateOnly? LastDischargeDate { get; set; }
}

/// <summary>
/// Which grouper the service asks for a DRG.
/// </summary>
public sealed class MsDrgGrouperOptions
{
    public const string SectionName = "MsDrgGrouper";

    /// <summary>
    /// The releases to load. Empty — the default — means no grouping: the
    /// claim's own DRG stands, exactly as before this seam existed.
    ///
    /// <para>
    /// No CMS table is in this repository and none can be. CMS distributes the
    /// MS-DRG package per requester and it carries American Hospital
    /// Association copyright in the UB-04 code descriptions, so a deployment
    /// points this at a release it holds its own licence for.
    /// </para>
    /// </summary>
    public List<MsDrgReleaseOptions> Releases { get; set; } = [];
}

/// <summary>
/// Binds the grouper seam from configuration.
/// </summary>
public static class MsDrgGrouperRegistration
{
    /// <summary>
    /// Registers an <see cref="IMsDrgGrouper"/>: CHO's own grouper over the
    /// configured releases, or <see cref="BilledDrgGrouper"/> when none is
    /// configured.
    ///
    /// <para>
    /// A configured release that cannot be imported is not swallowed. Falling
    /// back to the billed DRG there would silently pay every inpatient claim
    /// the facility's own number on a deployment that had asked to group, and
    /// the difference would show up as money rather than as an error.
    /// </para>
    /// </summary>
    public static IServiceCollection AddMsDrgGrouper(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = new MsDrgGrouperOptions();
        configuration.GetSection(MsDrgGrouperOptions.SectionName).Bind(options);

        services.AddScoped<IInpatientDrgResolver, GrouperInpatientDrgResolver>();

        if (options.Releases.Count == 0)
        {
            services.AddSingleton<IMsDrgGrouper, BilledDrgGrouper>();
            return services;
        }

        var windows = options.Releases
            .Select(release => new MsDrgReleaseWindow(
                CmsJsonTableImporter.ImportRelease(release.Directory, release.Version),
                release.FirstDischargeDate,
                release.LastDischargeDate))
            .ToList();

        services.AddSingleton<IMsDrgReleaseProvider>(new ReleaseWindowProvider(windows));
        services.AddSingleton<IMsDrgGrouper, FormulaMsDrgGrouper>();
        return services;
    }
}
