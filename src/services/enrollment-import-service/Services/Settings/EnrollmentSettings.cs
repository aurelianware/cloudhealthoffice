namespace EnrollmentImportService.Services.Settings;

/// <summary>
/// Enrollment's settings live in the plan configuration catalog
/// (docs/architecture/plan-configuration.md), stored and resolved by
/// tenant-service and read through <c>IPlanSettings</c>. This file keeps the
/// small shape the orphan rules record on an exception's actions.
/// </summary>
public static class EnrollmentSettingKeys
{
    /// <summary>Days a dependent whose subscriber is not on file is held before it reaches the Matching queue.</summary>
    public static readonly string OrphanGraceDays = CloudHealthOffice.PlanConfiguration.PlanSettings.OrphanGraceDays.Key;
}

/// <summary>
/// A resolved value and where it came from: <c>Default</c> (the platform
/// default, <see cref="SourceKey"/> naming the line of business), a level
/// name (<c>Tenant</c>, <c>LineOfBusiness</c>, <c>Sponsor</c>), or
/// <c>Unavailable</c> when tenant-service could not be read.
/// </summary>
public sealed record ResolvedEnrollmentSetting(string Setting, int Value, string Source, string? SourceKey)
{
    public static ResolvedEnrollmentSetting From(CloudHealthOffice.PlanConfiguration.ResolvedSetting resolved) => new(
        resolved.Key,
        resolved.AsInt(),
        resolved.IsFallback ? "Unavailable"
            : resolved.SourceLevel == CloudHealthOffice.PlanConfiguration.SettingLevel.Platform ? "Default"
            : resolved.SourceLevel.ToString(),
        resolved.SourceId);
}
