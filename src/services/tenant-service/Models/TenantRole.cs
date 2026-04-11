using System.Text.Json.Serialization;

namespace TenantService.Models;

/// <summary>
/// Defines a role with associated permissions for tenant RBAC.
/// Standard roles mirror how health plans organize their operations departments.
/// </summary>
public class TenantRole
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("roleName")]
    public string RoleName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = new();

    [JsonPropertyName("isBuiltIn")]
    public bool IsBuiltIn { get; set; } = false;
}

/// <summary>
/// DTO for creating a custom role
/// </summary>
public class CreateRoleRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    public string RoleName { get; set; } = string.Empty;

    public string? Description { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MinLength(1)]
    public List<string> Permissions { get; set; } = new();
}

/// <summary>
/// DTO for updating a custom role
/// </summary>
public class UpdateRoleRequest
{
    public string? Description { get; set; }
    public List<string>? Permissions { get; set; }
}

/// <summary>
/// Provides the default roles and permissions for the RBAC system.
/// These roles reflect actual health plan operations department structure.
/// </summary>
public static class StandardRoles
{
    public static readonly TenantRole ClaimsExaminer = new()
    {
        RoleName = "ClaimsExaminer",
        Description = "Process claims from assigned work queues",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "claims:read", "claims:work", "claims:override-request",
            "workqueue:read", "workqueue:work",
            "members:read", "accumulators:read", "providers:read"
        }
    };

    public static readonly TenantRole ClaimsSupervisor = new()
    {
        RoleName = "ClaimsSupervisor",
        Description = "Supervise claims operations, approve overrides, reassign work",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            // ClaimsExaminer permissions
            "claims:read", "claims:work", "claims:override-request",
            "workqueue:read", "workqueue:work",
            "members:read", "accumulators:read", "providers:read",
            // Supervisor-specific permissions
            "claims:override-approve", "workqueue:assign", "workqueue:reassign",
            "reports:claims", "claims:void", "claims:adjust"
        }
    };

    public static readonly TenantRole MemberServices = new()
    {
        RoleName = "MemberServices",
        Description = "Handle member inquiries, verify eligibility and benefits",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "members:read", "members:search", "accumulators:read",
            "eligibility:check", "claims:read", "coverage:read",
            "authorizations:read"
        }
    };

    public static readonly TenantRole EnrollmentSpecialist = new()
    {
        RoleName = "EnrollmentSpecialist",
        Description = "Process enrollment files, maintain member demographics",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "members:read", "members:write",
            "enrollment:read", "enrollment:process",
            "coverage:read", "coverage:write"
        }
    };

    public static readonly TenantRole UMCoordinator = new()
    {
        RoleName = "UMCoordinator",
        Description = "Manage prior authorizations, appeals, and clinical reviews",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "authorizations:read", "authorizations:write", "authorizations:decide",
            "appeals:read", "appeals:write",
            "rfai:read", "rfai:write",
            "correspondence:read", "correspondence:write",
            "members:read", "claims:read"
        }
    };

    public static readonly TenantRole ProviderRelations = new()
    {
        RoleName = "ProviderRelations",
        Description = "Manage provider directory, credentialing, and contracts",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "providers:read", "providers:write", "providers:credential",
            "contracts:read", "contracts:write",
            "networks:read", "networks:write"
        }
    };

    public static readonly TenantRole Finance = new()
    {
        RoleName = "Finance",
        Description = "Process payments, manage premium billing, financial reporting",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "payments:read", "payments:run", "payments:approve",
            "billing:read", "billing:run",
            "reports:financial", "claims:read"
        }
    };

    public static readonly TenantRole ComplianceOfficer = new()
    {
        RoleName = "ComplianceOfficer",
        Description = "Read-only access to all functions, audit logs, compliance reports",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "*:read", "audit:read", "compliance:read", "reports:compliance"
        }
    };

    public static readonly TenantRole ComplianceViewer = new()
    {
        RoleName = "ComplianceViewer",
        Description = "Read-only access to compliance reference data (e.g. TMPPM PA Rule Explorer) and authorization records",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "compliance:read", "authorizations:read", "audit:read"
        }
    };

    public static readonly TenantRole TenantAdmin = new()
    {
        RoleName = "TenantAdmin",
        Description = "Full administrative access, user management, system configuration",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "*:*", "users:manage", "roles:manage",
            "settings:manage", "operating-mode:manage"
        }
    };

    /// <summary>
    /// Returns all standard built-in roles
    /// </summary>
    public static IReadOnlyList<TenantRole> All => new List<TenantRole>
    {
        ClaimsExaminer,
        ClaimsSupervisor,
        MemberServices,
        EnrollmentSpecialist,
        UMCoordinator,
        ProviderRelations,
        Finance,
        ComplianceOfficer,
        ComplianceViewer,
        TenantAdmin
    };

    /// <summary>
    /// Checks if a user with the given roles has a specific permission.
    /// Supports wildcard matching: "*:read" matches "claims:read", "*:*" matches everything.
    /// </summary>
    public static bool HasPermission(IEnumerable<string> userRoles, string requiredPermission, IEnumerable<TenantRole> availableRoles)
    {
        var roleSet = new HashSet<string>(userRoles, StringComparer.OrdinalIgnoreCase);

        foreach (var role in availableRoles.Where(r => roleSet.Contains(r.RoleName)))
        {
            foreach (var permission in role.Permissions)
            {
                if (PermissionMatches(permission, requiredPermission))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a granted permission matches the required permission,
    /// supporting wildcard patterns like "*:read" and "*:*".
    /// </summary>
    private static bool PermissionMatches(string granted, string required)
    {
        if (string.Equals(granted, "*:*", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(granted, required, StringComparison.OrdinalIgnoreCase))
            return true;

        var grantedParts = granted.Split(':');
        var requiredParts = required.Split(':');

        if (grantedParts.Length != 2 || requiredParts.Length != 2)
            return false;

        var resourceMatch = grantedParts[0] == "*" ||
            string.Equals(grantedParts[0], requiredParts[0], StringComparison.OrdinalIgnoreCase);
        var actionMatch = grantedParts[1] == "*" ||
            string.Equals(grantedParts[1], requiredParts[1], StringComparison.OrdinalIgnoreCase);

        return resourceMatch && actionMatch;
    }
}
