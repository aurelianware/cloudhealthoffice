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

    public void Unavailable(EntraUser? user, string? tenantId, string reason)
        => _logger.LogError(
            "CHO token {Outcome}: tid={Tid} oid={Oid} tenant={Tenant} reason={Reason}",
            "refused", Clean(user?.Tid), Clean(user?.Oid), Clean(tenantId), Clean(reason));

    private static string Clean(string? value)
        => string.IsNullOrEmpty(value) ? "-" : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
