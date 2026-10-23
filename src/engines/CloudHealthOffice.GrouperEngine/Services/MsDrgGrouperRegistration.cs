using CloudHealthOffice.GrouperEngine.Mask;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.GrouperEngine.Services;

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
/// Which grouper a host asks for a DRG.
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
/// Binds the grouper from configuration, for any host that needs one.
///
/// <para>
/// This lives in the engine assembly rather than in a service because two
/// hosts now ask for a DRG — the Pricing API, which reprices against a named
/// fee schedule, and claims-service, which groups a claim in adjudication —
/// and a deployment that configured one release must get the same grouper in
/// both. Two copies of this binding would be two chances for the Pricing API
/// and adjudication to disagree about what a stay groups to, which is the one
/// disagreement that shows up as money.
/// </para>
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
    ///
    /// <para>
    /// <see cref="IMsDrgReleaseProvider"/> is registered only when a release
    /// was loaded, so a caller can inject it as nullable to ask whether this
    /// host can group at all. <see cref="InpatientGroupingStage"/> in
    /// claims-service uses that to decide how cautious to be when it cannot
    /// read the plan setting: a host with no tables could not have grouped
    /// whatever the setting said, so the unknown costs nothing there.
    /// </para>
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">Configuration holding <c>MsDrgGrouper</c>.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddMsDrgGrouper(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new MsDrgGrouperOptions();
        configuration.GetSection(MsDrgGrouperOptions.SectionName).Bind(options);

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
