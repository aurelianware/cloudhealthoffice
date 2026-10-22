namespace CloudHealthOffice.PlanConfiguration;

/// <summary>
/// The setting definitions CHO knows. Definitions ship here, in code, so the
/// catalog tenant-service serves and the definitions each owning service
/// resolves are the same build (plan-configuration.md §4). A service adds its
/// setting to <see cref="PlanSettings"/>; tenant-service needs no change to
/// accept values for it. A service can call <see cref="Require"/> at startup
/// to fail fast on a key that is not defined.
/// </summary>
public static class PlanSettingCatalog
{
    private static readonly IReadOnlyDictionary<string, SettingDefinition> ByKey =
        PlanSettings.All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static IReadOnlyCollection<SettingDefinition> All => (IReadOnlyCollection<SettingDefinition>)ByKey.Values;

    public static SettingDefinition? Find(string key) => ByKey.TryGetValue(key, out var d) ? d : null;

    public static SettingDefinition Require(string key) =>
        Find(key) ?? throw new InvalidOperationException($"Setting '{key}' is not defined in the plan configuration catalog.");
}

/// <summary>The catalog as a service, so tenant-service can be tested with definitions of its own.</summary>
public interface ISettingCatalog
{
    IReadOnlyCollection<SettingDefinition> All { get; }
    SettingDefinition? Find(string key);
}

/// <summary>The catalog shipped in code (<see cref="PlanSettings"/>).</summary>
public sealed class BuiltInSettingCatalog : ISettingCatalog
{
    public IReadOnlyCollection<SettingDefinition> All => PlanSettingCatalog.All;
    public SettingDefinition? Find(string key) => PlanSettingCatalog.Find(key);
}

/// <summary>Every defined setting. Add new ones here.</summary>
public static class PlanSettings
{
    /// <summary>
    /// Days a dependent whose subscriber is not on file is held before it
    /// reaches the Matching queue (enrollment-operations.md §10, decision 5).
    /// One file cycle for commercial groups; none where the sponsor sends
    /// whole enrollment groups (Marketplace) or is the source of truth
    /// (Medicaid). Benefit plan and sponsor policy levels join when those
    /// records exist.
    /// </summary>
    public static readonly SettingDefinition OrphanGraceDays = new()
    {
        Key = "enrollment.orphan.graceDays",
        Type = SettingType.Integer,
        Owner = "enrollment-import-service",
        Description = "Days an orphaned dependent (subscriber not on file) is held for its subscriber before it opens in the Matching queue.",
        Unit = "days",
        Default = "0",
        DefaultsByLineOfBusiness = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [LinesOfBusiness.Commercial] = "7",
            [LinesOfBusiness.Exchange] = "0",
            [LinesOfBusiness.Medicaid] = "0",
        },
        AllowedLevels = new HashSet<SettingLevel> { SettingLevel.Tenant, SettingLevel.LineOfBusiness, SettingLevel.Sponsor },
        Constraints = new SettingConstraints { Min = 0, Max = 90 },
        ChangeControl = ChangeControl.ReasonRequired,
    };

    public static IReadOnlyList<SettingDefinition> All { get; } = [OrphanGraceDays];
}
