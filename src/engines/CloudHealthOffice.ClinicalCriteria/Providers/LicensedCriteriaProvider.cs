using CloudHealthOffice.ClinicalCriteria.Abstractions;
using CloudHealthOffice.ClinicalCriteria.Configuration;
using CloudHealthOffice.ClinicalCriteria.Models;

namespace CloudHealthOffice.ClinicalCriteria.Providers;

/// <summary>
/// Base for a provider whose criteria content is licensed from a vendor.
///
/// Cloud Health Office distributes no InterQual or MCG content and no vendor
/// client. What it distributes is the shape of the integration: the fields
/// CHO sends (<see cref="CriteriaRequest"/>), the fields it expects back
/// (<see cref="CriteriaEvaluation"/>), the routing that decides when the
/// vendor is asked, and the fail-open behaviour around the call. A customer
/// with a licence writes one class — a transport that maps those two records
/// onto the vendor's API — registers it, and nothing above this line changes.
///
/// Until that class exists, an enabled provider throws
/// <see cref="ClinicalCriteriaConnectorMissingException"/> on first use.
/// <see cref="Services.ClinicalCriteriaService"/> turns that into an
/// inconclusive evaluation, so the request pends to a human reviewer and the
/// misconfiguration is logged by name — never silently treated as though the
/// guidelines had been consulted.
/// </summary>
public abstract class LicensedCriteriaProvider(LicensedCriteriaProviderOptions options) : IClinicalCriteriaProvider
{
    protected LicensedCriteriaProviderOptions Options { get; } = options;

    public abstract string ProviderId { get; }
    public abstract string DisplayName { get; }

    /// <summary>Configured means enabled, with an endpoint and a named credential.</summary>
    public virtual bool IsConfigured => Options.IsUsable;

    public virtual Task<CriteriaEvaluation> EvaluateAsync(CriteriaRequest request, CancellationToken ct = default) =>
        throw new ClinicalCriteriaConnectorMissingException(ProviderId);
}

/// <summary>
/// InterQual (Change Healthcare / Optum). Registered only when
/// <c>ClinicalCriteria:InterQual:Enabled</c> is true.
/// </summary>
public sealed class InterQualCriteriaProvider(LicensedCriteriaProviderOptions options)
    : LicensedCriteriaProvider(options)
{
    public const string Id = "interqual";

    public override string ProviderId => Id;
    public override string DisplayName => "InterQual (licensed content)";
}

/// <summary>
/// MCG Health care guidelines. Registered only when
/// <c>ClinicalCriteria:Mcg:Enabled</c> is true.
/// </summary>
public sealed class McgCriteriaProvider(LicensedCriteriaProviderOptions options)
    : LicensedCriteriaProvider(options)
{
    public const string Id = "mcg";

    public override string ProviderId => Id;
    public override string DisplayName => "MCG Health care guidelines (licensed content)";
}
