using System.Text.RegularExpressions;

namespace AppealsService.Services;

/// <summary>
/// Default State for each tenant's appeals and grievances, used when a
/// create request names none. A single-State Medicaid plan configures its
/// State once here instead of on every request; a multi-State tenant leaves
/// it unset and sends <c>state</c> per case.
///
/// <code>
/// "AppealJurisdictions": {
///   "Tenants": { "medi-cal-plan": "CA", "star-plan": "TX" }
/// }
/// </code>
/// </summary>
public sealed class AppealJurisdictionOptions
{
    public const string SectionName = "AppealJurisdictions";

    /// <summary>Tenant id → two-letter State code.</summary>
    public Dictionary<string, string> Tenants { get; set; } = new();
}

public interface IAppealJurisdictionProvider
{
    /// <summary>
    /// The State a case falls under: <paramref name="requested"/> when given,
    /// else the tenant's configured default, else <c>null</c> (federal rules only).
    /// Upper case.
    /// </summary>
    string? Resolve(string tenantId, string? requested);
}

/// <summary>
/// Reads <see cref="AppealJurisdictionOptions"/>. A malformed State code
/// fails construction, so a typo fails startup instead of silently falling
/// back to federal clocks.
/// </summary>
public sealed partial class ConfiguredAppealJurisdictionProvider : IAppealJurisdictionProvider
{
    private readonly Dictionary<string, string> _tenants;

    public ConfiguredAppealJurisdictionProvider(AppealJurisdictionOptions options)
    {
        _tenants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (tenantId, state) in options.Tenants)
        {
            if (!IsStateCode(state))
            {
                throw new InvalidOperationException(
                    $"{AppealJurisdictionOptions.SectionName}:Tenants:{tenantId} is '{state}', not a two-letter State code.");
            }
            _tenants[tenantId] = state.Trim().ToUpperInvariant();
        }
    }

    public string? Resolve(string tenantId, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim().ToUpperInvariant();
        return _tenants.TryGetValue(tenantId, out var state) ? state : null;
    }

    public static bool IsStateCode(string? value) => value is not null && StateCode().IsMatch(value.Trim());

    [GeneratedRegex("^[A-Za-z]{2}$")]
    private static partial Regex StateCode();
}
