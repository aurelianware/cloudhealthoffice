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

    /// <summary>Token subject of the caller that created the role. Never read from a request body.</summary>
    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; set; }

    /// <summary>Token subject of the caller that last changed the role. Never read from a request body.</summary>
    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; set; }
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
            "workspace:use",
            "claims:read", "claims:work", "claims:override-request", "claims:note",
            "workqueue:read", "workqueue:work",
            "members:read", "accumulators:read", "providers:read",
            "documents:read", "documents:write"
        }
    };

    public static readonly TenantRole ClaimsSupervisor = new()
    {
        RoleName = "ClaimsSupervisor",
        Description = "Supervise claims operations, approve overrides, reassign work",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "workspace:use",
            // ClaimsExaminer permissions
            "claims:read", "claims:work", "claims:override-request", "claims:note",
            "workqueue:read", "workqueue:work",
            "members:read", "accumulators:read", "providers:read",
            "documents:read", "documents:write",
            // Supervisor-specific permissions
            "claims:override-approve", "claims:override-edit", "workqueue:assign", "workqueue:reassign",
            "workqueue:admin",
            "reports:claims", "claims:void", "claims:adjust",
            "encounters:read", "encounters:write",
            "config:read"
        }
    };

    public static readonly TenantRole MemberServices = new()
    {
        RoleName = "MemberServices",
        Description = "Handle member inquiries, verify eligibility and benefits",
        IsBuiltIn = true,
        // Keep the directory entitlement checks aligned with CHO token enforcement.
        Permissions = CloudHealthOffice.Infrastructure.Security.ChoRolePermissions
            .ForRole("MemberServices").ToList()
    };

    public static readonly TenantRole EnrollmentSpecialist = new()
    {
        RoleName = "EnrollmentSpecialist",
        Description = "Process enrollment files, maintain member demographics",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "workspace:use",
            "members:read", "members:write",
            "enrollment:read", "enrollment:process",
            "coverage:read", "coverage:write", "benefits:read", "providers:read",
            "documents:read", "documents:write",
            "payer-to-payer:initiate",
            "config:read"
        }
    };

    public static readonly TenantRole UMCoordinator = new()
    {
        RoleName = "UMCoordinator",
        Description = "Manage prior authorizations, appeals, and clinical reviews",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "workspace:use",
            "authorizations:read", "authorizations:write", "authorizations:decide",
            "appeals:read", "appeals:write",
            "rfai:read", "rfai:write",
            "correspondence:read", "correspondence:write",
            "members:read", "claims:read",
            "documents:read", "documents:write", "documents:read-sensitive",
            "clinical:read"
        }
    };

    public static readonly TenantRole ProviderRelations = new()
    {
        RoleName = "ProviderRelations",
        Description = "Manage provider directory, credentialing, and contracts",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "workspace:use",
            "claims:read", "claims:note",
            "providers:read", "providers:write", "providers:credential",
            "contracts:read", "contracts:write",
            "networks:read", "networks:write"
        }
    };

    public static readonly TenantRole Finance = new()
    {
        RoleName = "Finance",
        Description = "Prepare payment runs, manage premium billing, financial reporting (cannot approve or release payments)",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "workspace:use",
            "payments:read", "payments:run",
            "billing:read", "billing:run",
            "reports:financial", "claims:read", "encounters:read",
            "risk-adjustment:read", "risk-adjustment:write",
            "reference-data:read"
        }
    };

    public static readonly TenantRole FinanceApprover = new()
    {
        RoleName = "FinanceApprover",
        Description = "Approve and release payments prepared by another user",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "workspace:use",
            "payments:read", "payments:approve",
            "billing:read",
            "finance:read", "reports:financial",
            "reference-data:read"
        }
    };

    public static readonly TenantRole ComplianceOfficer = new()
    {
        RoleName = "ComplianceOfficer",
        Description = "Read access to all functions, audit logs and compliance reports; sets and releases legal holds; approves letter templates",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "workspace:use",
            "*:read", "audit:read", "compliance:read", "reports:compliance",
            "records:legal-hold",
            // Compliance signs off the wording of member notices.
            "correspondence:approve",
            // Exact-only: *:read does not reach it.
            "documents:read-sensitive"
        }
    };

    public static readonly TenantRole ComplianceViewer = new()
    {
        RoleName = "ComplianceViewer",
        Description = "Read-only access to compliance reference data (e.g. TMPPM PA Rule Explorer) and authorization records",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "workspace:use",
            "compliance:read", "authorizations:read", "audit:read",
            "reference-data:read", "config:read"
        }
    };

    // Plan configuration roles (docs/architecture/plan-configuration.md §9a).
    public static readonly TenantRole AccountManager = new()
    {
        RoleName = "AccountManager",
        Description = "Enter a group's terms (sponsor, policy and class settings) while working new business and renewals",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "workspace:use", "config:read", "config:sponsor", "benefits:read", "reference-data:read"
        }
    };

    public static readonly TenantRole Underwriter = new()
    {
        RoleName = "Underwriter",
        Description = "Approve terms outside the product's negotiable ranges",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "workspace:use", "config:read", "config:approve", "benefits:read", "reference-data:read"
        }
    };

    public static readonly TenantRole ConfigurationAnalyst = new()
    {
        RoleName = "ConfigurationAnalyst",
        Description = "Build programs and plans, set negotiable ranges, configure sponsors",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "workspace:use", "config:read", "config:product", "config:sponsor", "benefits:read", "reference-data:read"
        }
    };

    public static readonly TenantRole ConfigurationManager = new()
    {
        RoleName = "ConfigurationManager",
        Description = "All plan configuration: tenant, carrier and line-of-business settings, locks, approvals and promotion",
        IsBuiltIn = true,
        Permissions = new List<string>
        {
            "workspace:use", "config:read", "config:sponsor", "config:product", "config:enterprise", "config:approve",
            "benefits:read", "reference-data:read"
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
        FinanceApprover,
        ComplianceOfficer,
        ComplianceViewer,
        AccountManager,
        Underwriter,
        ConfigurationAnalyst,
        ConfigurationManager,
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
    /// Checks if a granted permission matches the required permission, with
    /// the shared rules (wildcards such as "*:read" and "*:*" never reach
    /// platform:* permissions).
    /// </summary>
    private static bool PermissionMatches(string granted, string required)
        => CloudHealthOffice.Infrastructure.Security.ChoRolePermissions.Matches(granted, required);
}
