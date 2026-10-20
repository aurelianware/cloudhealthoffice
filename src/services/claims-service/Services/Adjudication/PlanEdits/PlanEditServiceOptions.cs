using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanEdits.Contracts;

namespace ClaimsService.Services.Adjudication.PlanEdits;

/// <summary>What <see cref="Stages.PlanEditsStage"/> does with the plan's edits.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanEditEnforcementMode
{
    /// <summary>Default — an edit that fires pends the claim for an examiner (pend code PLANEDIT).</summary>
    PendForReview,

    /// <summary>
    /// Edits are called and recorded on the stage result and in telemetry, but
    /// the claim is not pended: for proving a new plan service against live
    /// claims before it affects any of them.
    /// </summary>
    SoftValidation,
}

/// <summary>What happens to a claim when the plan's edit service gives no usable answer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanEditUnavailableMode
{
    /// <summary>
    /// Default — the claim pends SVCUNAVAIL, a transient pend: an examiner
    /// approval never overrides it, and the approval re-run asks again.
    /// </summary>
    Pend,

    /// <summary>
    /// The claim continues without the plan's edits; the stage result and
    /// telemetry say they were skipped. The plan accepts that its edits can be
    /// missed while its service is down.
    /// </summary>
    Pass,
}

/// <summary>One tenant's plan edit service.</summary>
public sealed class PlanEditTenantOptions
{
    /// <summary>Absolute https URL CHO posts each claim to.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The secret the plan and CHO share for <see cref="PlanEditSignature"/>,
    /// at least <see cref="PlanEditContract.MinSecretLength"/> characters.
    /// Supplied from a secret store, never committed configuration.
    /// </summary>
    public string? SigningSecret { get; set; }

    public PlanEditEnforcementMode Mode { get; set; } = PlanEditEnforcementMode.PendForReview;

    public PlanEditUnavailableMode WhenUnavailable { get; set; } = PlanEditUnavailableMode.Pend;

    /// <summary>
    /// Budget for one attempt, <see cref="MinTimeoutMilliseconds"/>–<see cref="MaxTimeoutMilliseconds"/>.
    /// A transient failure is retried once inside a total budget of twice this.
    /// </summary>
    public int TimeoutMilliseconds { get; set; } = DefaultTimeoutMilliseconds;

    public const int DefaultTimeoutMilliseconds = 3000;
    public const int MinTimeoutMilliseconds = 200;
    public const int MaxTimeoutMilliseconds = 15000;
}

/// <summary>
/// Per-tenant plan edit services (ADR 019), bound from
/// <c>Adjudication:PlanEdits:Tenants:{tenantId}</c>. A tenant with no entry
/// has no plan edit service and the stage passes its claims straight through.
/// </summary>
public sealed class PlanEditServiceOptions
{
    public const string SectionName = "Adjudication:PlanEdits";

    public Dictionary<string, PlanEditTenantOptions> Tenants { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The problems with this configuration; empty when it is usable. Checked
    /// at startup, so a misconfigured plan service stops the deployment rather
    /// than pending or passing claims at run time.
    /// </summary>
    public IReadOnlyList<string> Validate(ChoOutboundHosts choHosts, bool allowInsecureHttp)
    {
        var problems = new List<string>();
        foreach (var (tenantId, tenant) in Tenants)
        {
            var at = $"{SectionName}:Tenants:{tenantId}";
            if (string.IsNullOrWhiteSpace(tenantId))
                problems.Add($"{at}: the tenant id is empty");

            // On Linux "/path" parses as an absolute file: URI; it is still not a URL.
            if (!Uri.TryCreate(tenant.Endpoint, UriKind.Absolute, out var uri) || uri.IsFile || uri.IsUnc)
            {
                problems.Add($"{at}:Endpoint: an absolute URL is required");
            }
            else
            {
                if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp))
                    problems.Add($"{at}:Endpoint: must be https");
                if (!string.IsNullOrEmpty(uri.UserInfo))
                    problems.Add($"{at}:Endpoint: must not carry credentials");
                if (!string.IsNullOrEmpty(uri.Fragment))
                    problems.Add($"{at}:Endpoint: must not have a fragment");
                // A CHO host receives CHO service tokens from the outbound
                // token handler; a plan's service must never be one.
                if (choHosts.IsChoService(uri))
                    problems.Add($"{at}:Endpoint: {uri.Host} is a CHO service host; a plan edit service must run outside CHO's host list");
            }

            if (string.IsNullOrEmpty(tenant.SigningSecret) || tenant.SigningSecret.Length < PlanEditContract.MinSecretLength)
                problems.Add($"{at}:SigningSecret: at least {PlanEditContract.MinSecretLength} characters are required");

            if (tenant.TimeoutMilliseconds is < PlanEditTenantOptions.MinTimeoutMilliseconds or > PlanEditTenantOptions.MaxTimeoutMilliseconds)
                problems.Add($"{at}:TimeoutMilliseconds: must be {PlanEditTenantOptions.MinTimeoutMilliseconds}–{PlanEditTenantOptions.MaxTimeoutMilliseconds}");

            if (!Enum.IsDefined(tenant.Mode))
                problems.Add($"{at}:Mode: PendForReview or SoftValidation");
            if (!Enum.IsDefined(tenant.WhenUnavailable))
                problems.Add($"{at}:WhenUnavailable: Pend or Pass");
        }
        return problems;
    }
}
