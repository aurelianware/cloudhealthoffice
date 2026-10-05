using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Provides the current user's identity, roles, and permissions for RBAC enforcement.
/// </summary>
public interface IUserContextService
{
    Task<UserContext?> GetCurrentUserAsync();
    bool HasPermission(string permission);
    bool HasRole(string role);
    bool HasAnyRole(params string[] roles);
}

/// <summary>
/// Represents the authenticated user's identity and flattened permissions.
/// Cached per circuit lifetime (scoped service).
/// </summary>
public class UserContext
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public string Department { get; set; } = string.Empty;
    public HashSet<string> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string PrimaryRole => Roles.FirstOrDefault() ?? "Unknown";

    public string PrimaryRoleDisplayName => PrimaryRole switch
    {
        "ClaimsExaminer" => "Claims Examiner",
        "ClaimsSupervisor" => "Claims Supervisor",
        "MemberServices" => "Member Services",
        "EnrollmentSpecialist" => "Enrollment Specialist",
        "UMCoordinator" => "UM Coordinator",
        "ProviderRelations" => "Provider Relations",
        "Finance" => "Finance",
        "FinanceApprover" => "Finance Approver",
        "ComplianceOfficer" => "Compliance Officer",
        "ComplianceViewer" => "Compliance Viewer",
        "TenantAdmin" => "Tenant Admin",
        "PlatformAdmin" => "Platform Admin",
        _ => PrimaryRole
    };
}

public class UserContextService : IUserContextService
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly IChoTokenProvider _tokenProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserContextService> _logger;
    private readonly IHostEnvironment? _environment;
    private UserContext? _cachedContext;
    private bool _loaded;

    public UserContextService(
        AuthenticationStateProvider authenticationStateProvider,
        IChoTokenProvider tokenProvider,
        IConfiguration configuration,
        ILogger<UserContextService> logger,
        IHostEnvironment? environment = null)
    {
        _environment = environment;
        _authenticationStateProvider = authenticationStateProvider;
        _tokenProvider = tokenProvider;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// The signed-in user's CHO identity, tenant, roles and permissions, exactly as
    /// the CHO token service issued them in the token exchange. The portal does not
    /// look the user up or decide roles itself. If the exchange fails for any reason
    /// (no access, token service unavailable, consent required) the user has no roles.
    /// </summary>
    public async Task<UserContext?> GetCurrentUserAsync()
    {
        if (_loaded) return _cachedContext;

        var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        var principal = authState.User;

        if (!(principal.Identity?.IsAuthenticated ?? false))
        {
            _loaded = true;
            return null;
        }

        var email = principal.FindFirst(ClaimTypes.Email)?.Value
                    ?? principal.FindFirst("preferred_username")?.Value
                    ?? principal.FindFirst("upn")?.Value;

        if (string.IsNullOrEmpty(email))
        {
            _loaded = true;
            return null;
        }

        ChoTokenResult result;
        try
        {
            result = await _tokenProvider.GetTokenAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "CHO token exchange failed for user {ObjectId}", ObjectIdOf(principal));
            result = ChoTokenResult.Failure(ChoTokenStatus.Unavailable);
        }

        if (result.Succeeded)
        {
            _cachedContext = FromExchange(result.Token!, principal, email);
            _logger.LogDebug("User context loaded for user {ObjectId} with roles: {Roles}",
                ObjectIdOf(principal), string.Join(", ", _cachedContext.Roles));
        }
        else
        {
            _logger.LogWarning("No CHO token for user {ObjectId} ({Status}); the user has no roles",
                ObjectIdOf(principal), result.Status);
            _cachedContext = FallbackContext(principal, email, string.Empty);
        }

        _loaded = true;
        return _cachedContext;
    }

    private static UserContext FromExchange(ChoTokenExchangeResponse token, ClaimsPrincipal principal, string email)
    {
        var user = token.User ?? new ChoTokenUser();
        var displayName = FirstNonEmpty(user.DisplayName,
                              principal.FindFirst("name")?.Value,
                              principal.FindFirst(ClaimTypes.Name)?.Value)
                          ?? email;
        var roles = token.Roles?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? new List<string>();
        var permissions = new HashSet<string>(
            token.Permissions?.Where(p => !string.IsNullOrWhiteSpace(p)) ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        return new UserContext
        {
            UserId = user.Id ?? string.Empty,
            Email = FirstNonEmpty(user.Email) ?? email,
            DisplayName = displayName,
            FirstName = FirstNonEmpty(user.FirstName) ?? displayName.Split(' ').FirstOrDefault() ?? displayName,
            LastName = FirstNonEmpty(user.LastName) ?? displayName.Split(' ').Skip(1).FirstOrDefault() ?? "",
            TenantId = token.TenantId,
            Roles = roles,
            Department = user.Department ?? string.Empty,
            Permissions = permissions
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// The context for a signed-in user who has no CHO token (no access, token
    /// service unavailable, consent pending). It grants no roles. Only a
    /// Development host with <c>Authentication:AllowTenantAdminFallback</c> set
    /// gets TenantAdmin, for bootstrapping a local environment; backend services
    /// still refuse such a user, since no CHO token exists.
    /// </summary>
    private UserContext FallbackContext(ClaimsPrincipal principal, string email, string tenantId)
    {
        var displayName = principal.FindFirst("name")?.Value
                          ?? principal.FindFirst(ClaimTypes.Name)?.Value
                          ?? email;

        var grantAdmin = _environment?.IsDevelopment() == true
                         && string.Equals(_configuration["Authentication:AllowTenantAdminFallback"], "true",
                             StringComparison.OrdinalIgnoreCase);
        var roles = grantAdmin ? new List<string> { ChoRolePermissions.TenantAdmin } : new List<string>();

        if (grantAdmin)
            _logger.LogWarning("Granting development TenantAdmin fallback to user {ObjectId}", ObjectIdOf(principal));

        return new UserContext
        {
            UserId = "fallback",
            Email = email,
            DisplayName = displayName,
            FirstName = displayName.Split(' ').FirstOrDefault() ?? displayName,
            LastName = displayName.Split(' ').Skip(1).FirstOrDefault() ?? "",
            TenantId = tenantId,
            Roles = roles,
            Department = grantAdmin ? "Administration" : string.Empty,
            Permissions = ChoRolePermissions.Expand(roles)
        };
    }

    /// <summary>
    /// The user's Entra object id for logs. Logs never carry the email address,
    /// even partly masked (that still shows the domain and part of the name).
    /// </summary>
    private static string ObjectIdOf(ClaimsPrincipal principal)
        => principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
           ?? principal.FindFirst("oid")?.Value
           ?? "unknown";

    public bool HasPermission(string permission)
    {
        if (_cachedContext == null) return false;
        return PermissionMatches(_cachedContext.Permissions, permission);
    }

    public bool HasRole(string role)
    {
        if (_cachedContext == null) return false;
        return _cachedContext.Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }

    public bool HasAnyRole(params string[] roles)
    {
        if (_cachedContext == null) return false;
        return roles.Any(r => _cachedContext.Roles.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Check if a set of granted permissions (including wildcards) matches the required permission.
    /// </summary>
    private static bool PermissionMatches(HashSet<string> grantedPermissions, string required)
    {
        if (grantedPermissions.Contains(required))
            return true;

        var requiredParts = required.Split(':');
        if (requiredParts.Length != 2)
            return false;

        // platform:* permissions act across tenants; only an explicit grant
        // reaches them, never a tenant role's *:* or *:read.
        if (string.Equals(requiredParts[0], "platform", StringComparison.OrdinalIgnoreCase))
            return grantedPermissions.Contains("platform:*");

        if (grantedPermissions.Contains("*:*"))
            return true;

        // Check wildcard patterns like *:read
        if (grantedPermissions.Contains($"*:{requiredParts[1]}"))
            return true;

        // Check wildcard patterns like claims:*
        if (grantedPermissions.Contains($"{requiredParts[0]}:*"))
            return true;

        return false;
    }
}
