using CloudHealthOffice.ClinicalCriteria.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;

namespace CloudHealthOffice.ClinicalCriteria.Configuration;

/// <summary>
/// Configuration for the clinical criteria seam.
///
/// <code>
/// "ClinicalCriteria": {
///   "DefaultProvider": "cho-local",
///   "EvaluationTimeout": "00:00:10",
///   "Routing": [
///     { "Lob": "Medicare", "ReviewType": "Concurrent", "Provider": "interqual" },
///     { "TenantId": "acme", "Provider": "mcg" }
///   ],
///   "InterQual": { "Enabled": false, "BaseUrl": null, "CredentialSecretName": null },
///   "Mcg":       { "Enabled": false, "BaseUrl": null, "CredentialSecretName": null }
/// }
/// </code>
/// </summary>
public sealed class ClinicalCriteriaOptions
{
    public const string SectionName = "ClinicalCriteria";

    /// <summary>Provider used when no routing rule matches. The in-repo provider by default.</summary>
    public string DefaultProvider { get; set; } = LocalRulesProviderId;

    /// <summary>Provider id of the in-repo default, which needs no licence.</summary>
    public const string LocalRulesProviderId = "cho-local";

    /// <summary>
    /// How long one provider call may take before the evaluation is recorded
    /// as inconclusive and pended. A criteria vendor being slow must not hold
    /// a prior-authorization decision clock open.
    /// </summary>
    public TimeSpan EvaluationTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Rules matched in order of specificity: the rule that constrains the
    /// most fields wins, and ties go to the first configured. Every field left
    /// null matches anything.
    /// </summary>
    public List<CriteriaRoutingRule> Routing { get; set; } = [];

    public LicensedCriteriaProviderOptions InterQual { get; set; } = new();
    public LicensedCriteriaProviderOptions Mcg { get; set; } = new();
}

/// <summary>Sends some slice of the traffic to a named provider.</summary>
public sealed class CriteriaRoutingRule
{
    public string? TenantId          { get; set; }
    public string? StateCode         { get; set; }
    public PaLineOfBusiness? Lob     { get; set; }
    public string? Program           { get; set; }
    public CriteriaReviewType? ReviewType { get; set; }

    /// <summary>Provider id this slice goes to.</summary>
    public string Provider           { get; set; } = string.Empty;

    /// <summary>How many fields this rule constrains; the most specific match wins.</summary>
    internal int Specificity =>
        (TenantId   is null ? 0 : 1) +
        (StateCode  is null ? 0 : 1) +
        (Lob        is null ? 0 : 1) +
        (Program    is null ? 0 : 1) +
        (ReviewType is null ? 0 : 1);
}

/// <summary>
/// Connection settings for a licensed provider. No secret is held here: the
/// credential is named, and the host resolves it through its secret provider.
/// </summary>
public sealed class LicensedCriteriaProviderOptions
{
    /// <summary>
    /// False by default. While false the provider is not registered at all, so
    /// a deployment without a licence carries no inert vendor code path.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Vendor API base address, supplied with the licence.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Name of the secret holding the vendor credential, resolved through the
    /// host's secret provider. Never the credential itself.
    /// </summary>
    public string? CredentialSecretName { get; set; }

    /// <summary>Licensed content edition or release to evaluate against, when the vendor takes one.</summary>
    public string? ContentVersion { get; set; }

    /// <summary>Usable only with an endpoint and a named credential.</summary>
    public bool IsUsable =>
        Enabled
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(CredentialSecretName);
}
