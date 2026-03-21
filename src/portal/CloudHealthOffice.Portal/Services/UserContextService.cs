using System.Security.Claims;
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
        "ComplianceOfficer" => "Compliance Officer",
        "TenantAdmin" => "Tenant Admin",
        _ => PrimaryRole
    };
}

public class UserContextService : IUserContextService
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly ITenantContextService _tenantContextService;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserContextService> _logger;
    private UserContext? _cachedContext;
    private bool _loaded;

    public UserContextService(
        AuthenticationStateProvider authenticationStateProvider,
        ITenantContextService tenantContextService,
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<UserContextService> logger)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _tenantContextService = tenantContextService;
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

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

        var tenantContext = await _tenantContextService.GetCurrentTenantContextAsync();
        if (tenantContext == null)
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

        var objectId = principal.FindFirst("oid")?.Value
                       ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

        try
        {
            var baseUrl = _configuration["Services:TenantService"];
            if (string.IsNullOrEmpty(baseUrl))
            {
                _logger.LogWarning("Services:TenantService configuration is missing, using TenantAdmin fallback");
                throw new InvalidOperationException("TenantService base URL is not configured.");
            }

            HttpResponseMessage response;
            if (!string.IsNullOrEmpty(objectId))
            {
                response = await _httpClient.GetAsync(
                    $"{baseUrl}/v1/tenants/{tenantContext.TenantId}/users/by-oid/{Uri.EscapeDataString(objectId)}");
            }
            else
            {
                // Fallback: fetch all users and filter client-side
                response = await _httpClient.GetAsync(
                    $"{baseUrl}/v1/tenants/{tenantContext.TenantId}/users");
            }

            if (response.IsSuccessStatusCode)
            {
                TenantUserDto? user;
                if (!string.IsNullOrEmpty(objectId))
                {
                    user = await response.Content.ReadFromJsonAsync<TenantUserDto>();
                }
                else
                {
                    var users = await response.Content.ReadFromJsonAsync<List<TenantUserDto>>();
                    user = users?.FirstOrDefault(u =>
                        string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
                }

                if (user != null && string.Equals(user.Status, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    _cachedContext = new UserContext
                    {
                        UserId = user.Id,
                        Email = user.Email,
                        DisplayName = user.DisplayName,
                        FirstName = user.FirstName,
                        LastName = user.LastName,
                        TenantId = user.TenantId,
                        Roles = user.Roles ?? new(),
                        Department = user.Department,
                        Permissions = ExpandPermissions(user.Roles ?? new())
                    };

                    _logger.LogDebug("User context loaded for {RedactedEmail} with roles: {Roles}",
                        RedactEmail(email), string.Join(", ", _cachedContext.Roles));

                    _loaded = true;
                    return _cachedContext;
                }
            }

            _logger.LogDebug("No active TenantUser found for {RedactedEmail} in tenant {TenantId}, using fallback",
                RedactEmail(email), tenantContext.TenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch user context from tenant-service, using TenantAdmin fallback");
        }

        // Fallback: grant TenantAdmin so the portal doesn't break during development
        var displayName = principal.FindFirst("name")?.Value
                          ?? principal.FindFirst(ClaimTypes.Name)?.Value
                          ?? email;

        _cachedContext = new UserContext
        {
            UserId = "fallback",
            Email = email,
            DisplayName = displayName,
            FirstName = displayName.Split(' ').FirstOrDefault() ?? displayName,
            LastName = displayName.Split(' ').Skip(1).FirstOrDefault() ?? "",
            TenantId = tenantContext.TenantId,
            Roles = new List<string> { "TenantAdmin" },
            Department = "Administration",
            Permissions = ExpandPermissions(new List<string> { "TenantAdmin" })
        };

        _loaded = true;
        return _cachedContext;
    }

    private static string RedactEmail(string email)
    {
        var atIndex = email.IndexOf('@');
        if (atIndex <= 1) return "***@" + (atIndex >= 0 ? email[(atIndex + 1)..] : "***");
        return email[0] + "***" + email[(atIndex - 1)..];
    }

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
    /// Expand role names into the flat set of permissions based on standard role definitions.
    /// </summary>
    private static HashSet<string> ExpandPermissions(List<string> roles)
    {
        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in roles)
        {
            var perms = GetPermissionsForRole(role);
            foreach (var p in perms)
                permissions.Add(p);
        }

        return permissions;
    }

    private static List<string> GetPermissionsForRole(string roleName) => roleName switch
    {
        "ClaimsExaminer" => new()
        {
            "claims:read", "claims:work", "claims:override-request",
            "workqueue:read", "workqueue:work",
            "members:read", "accumulators:read", "providers:read"
        },
        "ClaimsSupervisor" => new()
        {
            "claims:read", "claims:work", "claims:override-request",
            "workqueue:read", "workqueue:work",
            "members:read", "accumulators:read", "providers:read",
            "claims:override-approve", "workqueue:assign", "workqueue:reassign",
            "reports:claims", "claims:void", "claims:adjust"
        },
        "MemberServices" => new()
        {
            "members:read", "members:search", "accumulators:read",
            "eligibility:check", "claims:read", "coverage:read",
            "authorizations:read"
        },
        "EnrollmentSpecialist" => new()
        {
            "members:read", "members:write",
            "enrollment:read", "enrollment:process",
            "coverage:read", "coverage:write"
        },
        "UMCoordinator" => new()
        {
            "authorizations:read", "authorizations:write", "authorizations:decide",
            "appeals:read", "appeals:write",
            "rfai:read", "rfai:write",
            "correspondence:read", "correspondence:write",
            "members:read", "claims:read"
        },
        "ProviderRelations" => new()
        {
            "providers:read", "providers:write", "providers:credential",
            "contracts:read", "contracts:write",
            "networks:read", "networks:write"
        },
        "Finance" => new()
        {
            "payments:read", "payments:run", "payments:approve",
            "billing:read", "billing:run",
            "reports:financial", "claims:read"
        },
        "ComplianceOfficer" => new()
        {
            "*:read", "audit:read", "compliance:read", "reports:compliance"
        },
        "TenantAdmin" => new()
        {
            "*:*", "users:manage", "roles:manage",
            "settings:manage", "operating-mode:manage"
        },
        _ => new()
    };

    /// <summary>
    /// Check if a set of granted permissions (including wildcards) matches the required permission.
    /// </summary>
    private static bool PermissionMatches(HashSet<string> grantedPermissions, string required)
    {
        if (grantedPermissions.Contains("*:*"))
            return true;

        if (grantedPermissions.Contains(required))
            return true;

        var requiredParts = required.Split(':');
        if (requiredParts.Length != 2)
            return false;

        // Check wildcard patterns like *:read
        if (grantedPermissions.Contains($"*:{requiredParts[1]}"))
            return true;

        // Check wildcard patterns like claims:*
        if (grantedPermissions.Contains($"{requiredParts[0]}:*"))
            return true;

        return false;
    }
}

/// <summary>
/// DTO matching the TenantUser model returned by tenant-service
/// </summary>
internal class TenantUserDto
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public string Department { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
