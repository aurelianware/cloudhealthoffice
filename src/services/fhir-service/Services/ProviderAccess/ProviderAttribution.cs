using Microsoft.Extensions.Options;

namespace FhirService.Services.ProviderAccess;

/// <summary>
/// Answers "is this provider in a treatment relationship with this member?" —
/// the control that keeps Provider Access to the provider's own panel rather
/// than the whole membership.
///
/// Independent of consent by design. A member may authorize Provider Access
/// generally and still not be this provider's patient; a provider may have the
/// member on their panel and still lack the member's authorization. Neither
/// control substitutes for the other, so both are asked and both must say yes.
/// </summary>
public interface IProviderAttributionSource
{
    /// <summary>
    /// True only when the tenant's attribution records place
    /// <paramref name="memberId"/> on <paramref name="providerId"/>'s panel.
    /// Fail-closed: unknown provider, unknown member, blank ids, or no catalog
    /// all return false.
    /// </summary>
    Task<bool> IsAttributedAsync(
        string tenantId, string providerId, string memberId, CancellationToken ct = default);
}

/// <summary>
/// Attribution panels as configuration, for Demo mode and tests.
///
/// This is the honest state of the capability: Cloud Health Office has no live
/// roster/panel integration, so attribution is served from a configured catalog
/// rather than a claimed feed from a source system. It enforces for real — an
/// empty catalog attributes no one — but it is not a claim that a payer's
/// attribution file is wired up. That remains engagement integration behind
/// this same seam.
/// </summary>
public sealed class ProviderAttributionOptions
{
    public const string SectionName = "Cms0057:ProviderAttribution";

    /// <summary>Attributed panels, keyed by tenant id.</summary>
    public Dictionary<string, List<ConfiguredProviderPanel>> PanelsByTenant { get; set; } = new();

    /// <summary>
    /// Attribution derived from the plan's own claims history. Off by default,
    /// so enabling it is a deployment decision that shows up in configuration
    /// review rather than something a clone starts doing silently.
    /// </summary>
    public ClaimsHistoryAttributionOptions ClaimsHistory { get; set; } = new();
}

/// <summary>
/// Attribution from treatment that actually happened: the provider appears as
/// the billing or rendering provider on one of the member's own claims inside
/// a lookback window.
///
/// <para>
/// This is what a payer with no roster file has, and it is what the rule
/// describes — a treatment relationship, not a contract. It does not replace
/// configured panels, because a panel can carry a relationship claims cannot
/// show yet: a member assigned to a PCP who has not yet been seen.
/// </para>
/// </summary>
public sealed class ClaimsHistoryAttributionOptions
{
    /// <summary>
    /// Whether to ask claims history at all. False leaves attribution exactly
    /// as it was, on configured panels alone.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How far back treatment counts, in months. Twenty-four is the common
    /// payer convention and the default; a plan with a different rule sets its
    /// own. A value of zero or less is a misconfiguration and is refused at
    /// start-up rather than read as "no lookback", which would attribute no one
    /// and look identical to a plan with no claims.
    /// </summary>
    public int LookbackMonths { get; set; } = 24;
}

/// <summary>One provider's attributed panel within a tenant.</summary>
public sealed class ConfiguredProviderPanel
{
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Members attributed to this provider. Empty attributes no one.</summary>
    public List<string> MemberIds { get; set; } = new();
}

/// <summary>
/// Configuration-backed attribution (Demo default, and the fallback when no
/// attribution feed is configured). An empty catalog attributes no one, so a
/// deployment that has not loaded panels denies Provider Access rather than
/// opening it.
/// </summary>
public sealed class ConfiguredProviderAttributionSource : IProviderAttributionSource
{
    private readonly IOptions<ProviderAttributionOptions> _options;

    public ConfiguredProviderAttributionSource(IOptions<ProviderAttributionOptions> options)
        => _options = options;

    public Task<bool> IsAttributedAsync(
        string tenantId, string providerId, string memberId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(memberId))
            return Task.FromResult(false);

        if (!_options.Value.PanelsByTenant.TryGetValue(tenantId, out var panels) || panels is null)
            return Task.FromResult(false);

        var attributed = panels.Any(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.Ordinal)
            && p.MemberIds is not null
            && p.MemberIds.Any(m => string.Equals(m, memberId, StringComparison.Ordinal)));

        return Task.FromResult(attributed);
    }
}
