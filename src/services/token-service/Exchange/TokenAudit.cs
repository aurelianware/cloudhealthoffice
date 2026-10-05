using CloudHealthOffice.TokenService.Entra;

namespace CloudHealthOffice.TokenService.Exchange;

/// <summary>
/// One structured audit line per issuance, refusal and link. Identifiers only:
/// tokens and email addresses are never logged.
/// </summary>
public sealed class TokenAudit
{
    private readonly ILogger _logger;

    public TokenAudit(ILoggerFactory loggerFactory)
        => _logger = loggerFactory.CreateLogger("CloudHealthOffice.TokenService.Audit");

    public void Issued(EntraUser user, string tenantId, string subject, IReadOnlyCollection<string> roles)
        => _logger.LogInformation(
            "CHO token {Outcome}: sub={Subject} tid={Tid} oid={Oid} tenant={Tenant} roles={Roles}",
            "issued", Clean(subject), Clean(user.Tid), Clean(user.Oid), Clean(tenantId), string.Join(",", roles.Select(Clean)));

    public void Refused(EntraUser user, string? tenantId, string? subject, string reason)
        => _logger.LogWarning(
            "CHO token {Outcome}: sub={Subject} tid={Tid} oid={Oid} tenant={Tenant} reason={Reason}",
            "refused", Clean(subject), Clean(user.Tid), Clean(user.Oid), Clean(tenantId), reason);

    public void Linked(EntraUser user, string tenantId, string subject)
        => _logger.LogInformation(
            "CHO identity {Outcome}: sub={Subject} tid={Tid} oid={Oid} tenant={Tenant}",
            "linked", Clean(subject), Clean(user.Tid), Clean(user.Oid), Clean(tenantId));

    /// <summary>
    /// One line per invitation redemption attempt, whatever its outcome. The code
    /// and the email addresses involved are never logged.
    /// </summary>
    public void InvitationRedemption(EntraUser? user, string outcome, string? tenantId, string? subject, string reason)
        => _logger.Log(outcome == "redeemed" ? LogLevel.Information : LogLevel.Warning,
            "CHO invitation {Outcome}: sub={Subject} tid={Tid} oid={Oid} tenant={Tenant} reason={Reason}",
            outcome, Clean(subject), Clean(user?.Tid), Clean(user?.Oid), Clean(tenantId), Clean(reason));

    public void Unavailable(EntraUser? user, string? tenantId, string reason)
        => _logger.LogError(
            "CHO token {Outcome}: tid={Tid} oid={Oid} tenant={Tenant} reason={Reason}",
            "refused", Clean(user?.Tid), Clean(user?.Oid), Clean(tenantId), Clean(reason));

    /// <summary>A workload token issued to a Kubernetes service account (Argo workflow).</summary>
    public void WorkloadIssued(Workload.KubernetesIdentity identity, string clientId, string tenantId, IReadOnlyCollection<string> permissions)
        => _logger.LogInformation(
            "CHO workload token {Outcome}: client={Client} sa={Namespace}/{ServiceAccount} pod={Pod} tenant={Tenant} permissions={Permissions}",
            "issued", Clean(clientId), Clean(identity.Namespace), Clean(identity.ServiceAccount), Clean(identity.PodName),
            Clean(tenantId), string.Join(",", permissions.Select(Clean)));

    /// <summary>A refused workload exchange. The identity is null when the Kubernetes token itself was refused.</summary>
    public void WorkloadRefused(Workload.KubernetesIdentity? identity, string? clientId, string? tenantId, string reason)
        => _logger.LogWarning(
            "CHO workload token {Outcome}: client={Client} sa={Namespace}/{ServiceAccount} pod={Pod} tenant={Tenant} reason={Reason}",
            "refused", Clean(clientId), Clean(identity?.Namespace), Clean(identity?.ServiceAccount), Clean(identity?.PodName),
            Clean(tenantId), Clean(reason));

    public void WorkloadUnavailable(Workload.KubernetesIdentity? identity, string? tenantId, string reason)
        => _logger.LogError(
            "CHO workload token {Outcome}: sa={Namespace}/{ServiceAccount} pod={Pod} tenant={Tenant} reason={Reason}",
            "refused", Clean(identity?.Namespace), Clean(identity?.ServiceAccount), Clean(identity?.PodName),
            Clean(tenantId), Clean(reason));

    /// <summary>A service token issued to a CHO service's managed identity.</summary>
    public void ServiceIssued(ServiceTokens.EntraWorkloadIdentity identity, string clientId, string tenantId)
        => _logger.LogInformation(
            "CHO service token {Outcome}: client={Client} oid={Oid} appid={AppId} tenant={Tenant}",
            "issued", Clean(clientId), identity.ObjectId, Clean(identity.AppId), Clean(tenantId));

    /// <summary>A refused service-token request. The identity is null when the Entra token itself was refused.</summary>
    public void ServiceRefused(ServiceTokens.EntraWorkloadIdentity? identity, string? clientId, string? tenantId, string reason)
        => _logger.LogWarning(
            "CHO service token {Outcome}: client={Client} oid={Oid} appid={AppId} tenant={Tenant} reason={Reason}",
            "refused", Clean(clientId), identity?.ObjectId.ToString() ?? "-", Clean(identity?.AppId), Clean(tenantId), Clean(reason));

    public void ServiceUnavailable(ServiceTokens.EntraWorkloadIdentity? identity, string? tenantId, string reason)
        => _logger.LogError(
            "CHO service token {Outcome}: oid={Oid} tenant={Tenant} reason={Reason}",
            "refused", identity?.ObjectId.ToString() ?? "-", Clean(tenantId), Clean(reason));

    private static string Clean(string? value)
        => string.IsNullOrEmpty(value) ? "-"
            : value.Length > 200 ? value[..200].Replace("\r", string.Empty).Replace("\n", string.Empty) + "…"
            : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
