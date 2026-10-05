using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// Mints short-lived CHO access tokens. Used by the portal (acting for a signed-in
/// user, after it has resolved the user's tenant membership and roles) and by
/// services (acting as themselves for a named tenant, when no user token is
/// available to forward).
/// </summary>
public sealed class ChoTokenIssuer
{
    private readonly string _issuer;
    private readonly string _audience;
    private readonly SigningCredentials _credentials;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;

    public ChoTokenIssuer(
        string issuer,
        string audience,
        SigningCredentials credentials,
        TimeSpan lifetime,
        TimeProvider? time = null)
    {
        _issuer = issuer;
        _audience = audience;
        _credentials = credentials;
        _lifetime = lifetime;
        _time = time ?? TimeProvider.System;
    }

    public static ChoTokenIssuer FromKeys(
        string issuer, string audience, string? privateKeyPem, string? symmetricKey, TimeSpan lifetime)
        => new(issuer, audience, ChoKeys.SigningCredentialsFrom(privateKeyPem, symmetricKey), lifetime);

    /// <summary>A token for a human user acting in <paramref name="tenantId"/>.</summary>
    public string IssueUserToken(
        string subject,
        string tenantId,
        IEnumerable<string> roles,
        IEnumerable<string>? permissions = null,
        string? name = null,
        string? email = null)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("Subject is required.", nameof(subject));
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant is required.", nameof(tenantId));

        var roleList = roles
            .Where(r => !string.Equals(r, ChoServiceRole.Name, StringComparison.OrdinalIgnoreCase))
            .Where(r => !string.Equals(r, ChoWorkloadRole.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var claims = new Dictionary<string, object>
        {
            [ChoClaimTypes.TenantId] = tenantId,
            [ChoClaimTypes.Role] = roleList.ToArray(),
            [ChoClaimTypes.Permission] = (permissions ?? ChoRolePermissions.Expand(roleList)).ToArray(),
        };
        if (!string.IsNullOrWhiteSpace(name)) claims[ChoClaimTypes.Name] = name;
        if (!string.IsNullOrWhiteSpace(email)) claims[ChoClaimTypes.Email] = email;

        return Create(subject, claims);
    }

    /// <summary>A token for a service acting as itself within <paramref name="tenantId"/>.</summary>
    public string IssueServiceToken(string clientId, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Client id is required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant is required.", nameof(tenantId));

        return Create(clientId, new Dictionary<string, object>
        {
            [ChoClaimTypes.TenantId] = tenantId,
            [ChoClaimTypes.Role] = new[] { ChoServiceRole.Name },
            ["azp"] = clientId,
        });
    }

    /// <summary>
    /// A token for a Kubernetes workload (token-service's workload exchange):
    /// <c>sub</c> = <c>azp</c> = <paramref name="clientId"/>, role
    /// <c>cho.workload</c> (which grants nothing) and exactly
    /// <paramref name="permissions"/>. Never a service token.
    /// </summary>
    public string IssueWorkloadToken(string clientId, string tenantId, IEnumerable<string> permissions)
    {
        if (!ChoWorkloadRole.IsWorkloadClientId(clientId))
            throw new ArgumentException($"A workload client id starts with '{ChoWorkloadRole.ClientIdPrefix}'.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant is required.", nameof(tenantId));

        var permissionList = permissions.ToArray();
        if (permissionList.Length == 0 || permissionList.Any(ChoRolePermissions.IsReserved))
            throw new ArgumentException("A workload token lists at least one permission and no platform permission.", nameof(permissions));

        return Create(clientId, new Dictionary<string, object>
        {
            [ChoClaimTypes.TenantId] = tenantId,
            [ChoClaimTypes.Role] = new[] { ChoWorkloadRole.Name },
            [ChoClaimTypes.Permission] = permissionList,
            [ChoClaimTypes.AuthorizedParty] = clientId,
        });
    }

    private string Create(string subject, Dictionary<string, object> claims)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        claims[ChoClaimTypes.Subject] = subject;
        claims[JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N");

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _issuer,
            Audience = _audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(_lifetime),
            Claims = claims,
            SigningCredentials = _credentials,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
