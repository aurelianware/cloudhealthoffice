namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// Claim names in a CHO access token. Inbound claim mapping is switched off,
/// so these are the literal JWT claim names.
/// </summary>
public static class ChoClaimTypes
{
    /// <summary>The CHO tenant the token acts within. The only source of tenancy.</summary>
    public const string TenantId = "tenant_id";

    /// <summary>Stable identifier of the acting user or service.</summary>
    public const string Subject = "sub";

    /// <summary>Display name of the acting user.</summary>
    public const string Name = "name";

    /// <summary>Email of the acting user, when known.</summary>
    public const string Email = "email";

    /// <summary>CHO role names (e.g. ClaimsExaminer). Multi-valued.</summary>
    public const string Role = "roles";

    /// <summary>Flattened permission strings (e.g. claims:read). Multi-valued.</summary>
    public const string Permission = "permissions";

    /// <summary>The client a service token was issued to (equals <c>sub</c> on service tokens).</summary>
    public const string AuthorizedParty = "azp";
}

/// <summary>
/// Reserved role a service token carries. Honoured only from issuers configured
/// with <see cref="ChoTrustedIssuer.AllowServiceRole"/>, so a user-token issuer
/// (the portal) can never mint a service identity.
/// </summary>
public static class ChoServiceRole
{
    public const string Name = "cho.service";
}

/// <summary>
/// Reserved role a workload token carries: a token token-service issues to a
/// Kubernetes workload (an Argo workflow) for its registered client id. The
/// role grants nothing by itself; the token's explicit <c>permissions</c> are
/// all it may do. It identifies a workload only from an issuer configured with
/// <see cref="ChoTrustedIssuer.AllowWorkloadIdentity"/>, so neither a user-token
/// issuer nor the shared service-token issuer can mint one.
/// </summary>
public static class ChoWorkloadRole
{
    public const string Name = "cho.workload";

    /// <summary>
    /// Every workload client id starts with this prefix and no service client
    /// id may. <see cref="ChoPrincipal.ServiceClientId"/> keeps the two apart: a
    /// service token naming a <c>wf-</c> client, or a workload token naming
    /// any other client, identifies no client at all.
    /// </summary>
    public const string ClientIdPrefix = "wf-";

    public static bool IsWorkloadClientId(string? clientId)
        => clientId != null && clientId.StartsWith(ClientIdPrefix, StringComparison.Ordinal);
}
