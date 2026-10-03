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
