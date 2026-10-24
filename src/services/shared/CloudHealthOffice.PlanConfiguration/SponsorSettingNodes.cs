namespace CloudHealthOffice.PlanConfiguration;

/// <summary>
/// How a sponsor and one of its policies are named as nodes of the setting
/// hierarchy, for a service that has to build the name rather than read one
/// it was handed (plan-configuration.md §12.16).
/// </summary>
/// <remarks>
/// sponsor-service is the origin of both forms: a sponsor node is the group
/// number, and <c>SponsorPolicy.IdFor</c> builds a policy node as
/// <c>{tenantId}|{groupNumber}|{policyNumber}</c> with the policy number
/// trimmed and upper-cased. That format is restated here because
/// sponsor-service publishes no contracts package, so nothing else can call
/// its builder; <c>SponsorSettingNodeTests</c> pins the exact strings so a
/// change on either side fails loudly.
///
/// <para>
/// Getting this wrong does not throw. A policy node named by the bare policy
/// number matches no stored value, so every setting entered at policy level
/// resolves to the tenant default and the claim is priced as though the
/// policy had never been configured — the failure this whole step exists to
/// prevent. Hence a named helper with a pinning test rather than string
/// interpolation at the call site.
/// </para>
///
/// <para>
/// If a fourth service needs these, the answer is a sponsor contracts package
/// rather than a fourth copy.
/// </para>
/// </remarks>
public static class SponsorSettingNodes
{
    /// <summary>
    /// The sponsor node: the group number as entered, trimmed. Deliberately
    /// not upper-cased — unlike the policy number, sponsor-service does not
    /// normalise a group number's case, so folding it here would stop
    /// matching values already stored against a mixed-case group.
    /// </summary>
    public static string? ForSponsor(string? groupNumber) =>
        string.IsNullOrWhiteSpace(groupNumber) ? null : groupNumber.Trim();

    /// <summary>
    /// The policy node, or null when either part is missing — a policy node
    /// cannot be named without the group it belongs to, and a half-built id
    /// would match nothing while looking like a real scope.
    /// </summary>
    public static string? ForPolicy(string tenantId, string? groupNumber, string? policyNumber)
    {
        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(groupNumber)
            || string.IsNullOrWhiteSpace(policyNumber))
        {
            return null;
        }

        return $"{tenantId.Trim()}|{groupNumber.Trim()}|{policyNumber.Trim().ToUpperInvariant()}";
    }
}
