using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService.Directory;
using CloudHealthOffice.TokenService.Entra;
using CloudHealthOffice.TokenService.Signing;

namespace CloudHealthOffice.TokenService.Exchange;

/// <summary>
/// Decides whether a validated Entra user may act in a CHO tenant, and with
/// which roles. Every input comes from the validated Entra token or from
/// tenant-service; nothing the caller (the portal) says about the user is used.
/// </summary>
public sealed class TokenExchangeService
{
    private readonly ITenantDirectory _directory;
    private readonly ISigningMaterialSource _signing;
    private readonly TokenSigningOptions _signingOptions;
    private readonly TokenServiceOptions _options;
    private readonly TokenAudit _audit;

    public TokenExchangeService(
        ITenantDirectory directory,
        ISigningMaterialSource signing,
        TokenSigningOptions signingOptions,
        TokenServiceOptions options,
        TokenAudit audit)
    {
        _directory = directory;
        _signing = signing;
        _signingOptions = signingOptions;
        _options = options;
        _audit = audit;
    }

    /// <summary>
    /// Platform administration needs both a directory and an app role: the app
    /// role alone is not enough, because in a multi-tenant app every customer
    /// directory's administrators can assign the app's roles to their own users.
    /// </summary>
    public bool IsPlatformAdmin(EntraUser user)
        => !string.IsNullOrWhiteSpace(_options.PlatformTenantId)
           && string.Equals(user.Tid, _options.PlatformTenantId, StringComparison.OrdinalIgnoreCase)
           && user.AppRoles.Contains(_options.PlatformAdminAppRole, StringComparer.Ordinal);

    /// <summary>The CHO tenants this user may enter.</summary>
    public async Task<IReadOnlyList<TenantChoice>> GetAccessibleTenantsAsync(EntraUser user, CancellationToken ct)
    {
        if (IsPlatformAdmin(user))
        {
            var all = await _directory.GetTenantsAsync(null, ct);
            return all.Where(t => t.IsActive).Select(TenantChoice.From).DistinctBy(t => t.TenantId).ToList();
        }

        var result = new Dictionary<string, TenantChoice>(StringComparer.Ordinal);

        foreach (var m in await _directory.GetMembershipsAsync(user.Tid, user.Oid, ct))
        {
            if (m.Tenant is { IsActive: true } tenant && IsHonouredLink(m.User, tenant, user) && m.User.IsActive)
                result.TryAdd(tenant.TenantId, TenantChoice.From(tenant));
        }

        // Tenants registered to the user's own directory, where an unlinked
        // record with the user's address would be linked on first exchange.
        if (!string.IsNullOrEmpty(user.Username))
        {
            foreach (var tenant in await _directory.GetTenantsAsync(user.Tid, ct))
            {
                if (!tenant.IsActive || result.ContainsKey(tenant.TenantId) || !OwnDirectory(tenant, user))
                    continue;
                var candidate = await _directory.FindUserByEmailAsync(tenant.TenantId, user.Username, ct);
                if (candidate is { IsActive: true } && IsLinkableByEmail(candidate, user))
                    result.TryAdd(tenant.TenantId, TenantChoice.From(tenant));
            }
        }

        return result.Values.ToList();
    }

    public async Task<ExchangeOutcome> ExchangeAsync(EntraUser user, string? requestedTenantId, CancellationToken ct)
    {
        var platformAdmin = IsPlatformAdmin(user);
        string tenantId;

        if (string.IsNullOrWhiteSpace(requestedTenantId))
        {
            var accessible = await GetAccessibleTenantsAsync(user, ct);
            var home = accessible.Where(t => string.Equals(t.AzureTenantId, user.Tid, StringComparison.OrdinalIgnoreCase)).ToList();
            var pick = home.Count == 1 ? home[0]
                : home.Count == 0 && accessible.Count == 1 ? accessible[0]
                : null;

            if (pick == null && accessible.Count == 0)
                return Refuse(user, null, null, "no_accessible_tenant");
            if (pick == null)
            {
                _audit.Refused(user, null, null, "tenant_required");
                return ExchangeOutcome.TenantRequired(accessible);
            }
            tenantId = pick.TenantId;
        }
        else
        {
            tenantId = requestedTenantId.Trim();
        }

        var tenant = await _directory.GetTenantAsync(tenantId, ct);
        if (tenant == null)
            return Refuse(user, tenantId, null, "unknown_tenant");
        if (!tenant.IsActive)
            return Refuse(user, tenantId, null, "tenant_inactive");

        var member = await ResolveMemberAsync(user, tenant, ct);

        if (ReferenceEquals(member, Ambiguous))
            return Refuse(user, tenantId, null, "duplicate_link");
        if (member != null && !member.IsActive)
            return Refuse(user, tenantId, member.Id, "user_inactive");

        if (member == null && !platformAdmin)
            return Refuse(user, tenantId, null, "no_membership");

        // PlatformAdmin is granted only by the rule above, never by a tenant's
        // own role assignment, which a tenant administrator controls.
        var roles = (member?.Roles ?? new List<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Where(r => !string.Equals(r, ChoRolePermissions.PlatformAdmin, StringComparison.OrdinalIgnoreCase))
            .Where(r => !string.Equals(r, ChoServiceRole.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (platformAdmin)
            roles.Add(ChoRolePermissions.PlatformAdmin);

        var permissions = ChoRolePermissions.Expand(roles).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var subject = member?.Id ?? $"platform:{user.Tid}:{user.Oid}";

        var display = member?.DisplayName is { Length: > 0 } dn ? dn : user.Name ?? string.Empty;
        var email = member?.Email is { Length: > 0 } em ? em : user.Username ?? string.Empty;

        string token;
        try
        {
            var material = await _signing.GetAsync(ct);
            var issuer = new ChoTokenIssuer(_signingOptions.Issuer, _signingOptions.Audience, material.Credentials, _options.TokenLifetime);
            token = issuer.IssueUserToken(subject, tenant.TenantId, roles, permissions,
                name: string.IsNullOrEmpty(display) ? null : display,
                email: string.IsNullOrEmpty(email) ? null : email);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Key Vault unreachable or refusing: no token, and the caller is told to retry.
            throw new SigningUnavailableException(ex);
        }

        _audit.Issued(user, tenant.TenantId, subject, roles);

        return ExchangeOutcome.Issued(new ExchangeResponse
        {
            AccessToken = token,
            ExpiresIn = (int)_options.TokenLifetime.TotalSeconds,
            TenantId = tenant.TenantId,
            TenantName = tenant.TenantName,
            Roles = roles,
            Permissions = permissions,
            User = new ExchangeUser
            {
                Id = subject,
                Email = email,
                DisplayName = display,
                FirstName = member?.FirstName is { Length: > 0 } fn ? fn : user.GivenName ?? string.Empty,
                LastName = member?.LastName is { Length: > 0 } ln ? ln : user.FamilyName ?? string.Empty,
                Department = member?.Department ?? string.Empty,
            },
        });
    }

    /// <summary>
    /// The TenantUser this Entra user is, in <paramref name="tenant"/>, or null.
    /// 1. A user linked to this oid, in this directory (or, for a link recorded
    ///    before directories were, only when this is the tenant's own directory).
    /// 2. First login: an unlinked user with this address, only when the token
    ///    comes from the tenant's own registered directory. The link is recorded.
    /// </summary>
    private async Task<DirectoryUser?> ResolveMemberAsync(EntraUser user, DirectoryTenant tenant, CancellationToken ct)
    {
        var linked = (await _directory.GetMembershipsAsync(user.Tid, user.Oid, ct))
            .Where(m => string.Equals(m.User.TenantId, tenant.TenantId, StringComparison.Ordinal))
            .Select(m => m.User)
            .Where(u => IsHonouredLink(u, tenant, user))
            .ToList();

        if (linked.Count > 1)
        {
            // Two records in one tenant for one identity: refuse rather than pick.
            return Ambiguous;
        }

        if (linked.Count == 1)
        {
            var found = linked[0];
            if (string.IsNullOrEmpty(found.AzureAdTenantId))
            {
                // Legacy link (oid only), honoured from the tenant's own
                // directory: record the directory so both must match from now on.
                found = await _directory.LinkAsync(tenant.TenantId, found.Id, user.Oid, user.Tid, ct) ?? found;
            }
            return found;
        }

        if (string.IsNullOrEmpty(user.Username) || !OwnDirectory(tenant, user))
            return null;

        var candidate = await _directory.FindUserByEmailAsync(tenant.TenantId, user.Username, ct);
        if (candidate == null || !IsLinkableByEmail(candidate, user))
            return null;

        var linkedNow = await _directory.LinkAsync(tenant.TenantId, candidate.Id, user.Oid, user.Tid, ct);
        if (linkedNow == null)
        {
            _audit.Refused(user, tenant.TenantId, candidate.Id, "link_conflict");
            return null;
        }

        _audit.Linked(user, tenant.TenantId, linkedNow.Id);
        return linkedNow;
    }

    /// <summary>Marker: more than one record in the tenant is linked to this identity.</summary>
    private static readonly DirectoryUser Ambiguous = new();

    private static bool OwnDirectory(DirectoryTenant tenant, EntraUser user)
        => !string.IsNullOrEmpty(tenant.AzureTenantId)
           && string.Equals(tenant.AzureTenantId, user.Tid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The record is linked to this oid, and its recorded directory is this
    /// token's tid. A record with no recorded directory (linked before it was
    /// stored, possibly by the portal's old email backfill) is honoured only from
    /// the tenant's own directory.
    /// </summary>
    private static bool IsHonouredLink(DirectoryUser u, DirectoryTenant tenant, EntraUser user)
    {
        if (!string.Equals(u.AzureAdObjectId, user.Oid, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(u.AzureAdTenantId))
            return string.Equals(u.AzureAdTenantId, user.Tid, StringComparison.OrdinalIgnoreCase);
        return OwnDirectory(tenant, user);
    }

    private static bool IsLinkableByEmail(DirectoryUser candidate, EntraUser user)
        => string.IsNullOrEmpty(candidate.AzureAdObjectId)
           && !string.IsNullOrEmpty(candidate.Email)
           && string.Equals(candidate.Email.Trim(), user.Username!.Trim(), StringComparison.OrdinalIgnoreCase);

    private ExchangeOutcome Refuse(EntraUser user, string? tenantId, string? subject, string reason)
    {
        _audit.Refused(user, tenantId, subject, reason);
        return ExchangeOutcome.NoAccess();
    }
}

public sealed class SigningUnavailableException(Exception inner)
    : Exception("The CHO token signing key is unavailable.", inner);

public sealed class TenantChoice
{
    [JsonPropertyName("tenantId")] public string TenantId { get; init; } = string.Empty;
    [JsonPropertyName("tenantName")] public string TenantName { get; init; } = string.Empty;
    [JsonPropertyName("azureTenantId")] public string AzureTenantId { get; init; } = string.Empty;

    public static TenantChoice From(DirectoryTenant t) => new()
    {
        TenantId = t.TenantId,
        TenantName = t.TenantName,
        AzureTenantId = t.AzureTenantId ?? string.Empty,
    };
}

public sealed class ExchangeResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
    [JsonPropertyName("token_type")] public string TokenType { get; init; } = "Bearer";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("tenant_id")] public string TenantId { get; init; } = string.Empty;
    [JsonPropertyName("tenant_name")] public string TenantName { get; init; } = string.Empty;
    [JsonPropertyName("roles")] public IReadOnlyList<string> Roles { get; init; } = [];
    [JsonPropertyName("permissions")] public IReadOnlyList<string> Permissions { get; init; } = [];
    [JsonPropertyName("user")] public ExchangeUser User { get; init; } = new();
}

public sealed class ExchangeUser
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = string.Empty;
    [JsonPropertyName("firstName")] public string FirstName { get; init; } = string.Empty;
    [JsonPropertyName("lastName")] public string LastName { get; init; } = string.Empty;
    [JsonPropertyName("department")] public string Department { get; init; } = string.Empty;
}

public sealed class ExchangeOutcome
{
    public ExchangeResponse? Response { get; private init; }
    public IReadOnlyList<TenantChoice>? Choices { get; private init; }
    public bool Denied { get; private init; }

    public static ExchangeOutcome Issued(ExchangeResponse r) => new() { Response = r };
    public static ExchangeOutcome NoAccess() => new() { Denied = true };
    public static ExchangeOutcome TenantRequired(IReadOnlyList<TenantChoice> choices) => new() { Choices = choices };
}
