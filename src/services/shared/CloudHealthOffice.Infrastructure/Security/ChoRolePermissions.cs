namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// The single definition of CHO's built-in roles and the permissions each one
/// grants. The portal (screen gating), tenant-service (role catalogue) and every
/// backend service (API enforcement) read this one table, so the three cannot
/// drift apart.
/// </summary>
public static class ChoRolePermissions
{
    public const string ClaimsExaminer = "ClaimsExaminer";
    public const string ClaimsSupervisor = "ClaimsSupervisor";
    public const string MemberServices = "MemberServices";
    public const string EnrollmentSpecialist = "EnrollmentSpecialist";
    public const string UMCoordinator = "UMCoordinator";
    public const string ProviderRelations = "ProviderRelations";
    public const string Finance = "Finance";
    public const string FinanceApprover = "FinanceApprover";
    public const string ComplianceOfficer = "ComplianceOfficer";
    public const string ComplianceViewer = "ComplianceViewer";
    public const string TenantAdmin = "TenantAdmin";
    public const string PlatformAdmin = "PlatformAdmin";

    private static readonly string[] ExaminerPermissions =
    [
        "claims:read", "claims:work", "claims:override-request",
        "workqueue:read", "workqueue:work",
        "members:read", "accumulators:read", "providers:read",
        "terminology:read", "reference-data:read",
        "attachments:read", "attachments:write", "benefits:read"
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Map =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [ClaimsExaminer] = ExaminerPermissions,
            [ClaimsSupervisor] =
            [
                .. ExaminerPermissions,
                "claims:override-approve", "workqueue:assign", "workqueue:reassign",
                "reports:claims", "claims:void", "claims:adjust", "trading-partners:read",
                "encounters:read", "encounters:write"
            ],
            [MemberServices] =
            [
                "members:read", "members:search", "accumulators:read",
                "eligibility:check", "claims:read", "coverage:read",
                "authorizations:read", "reference-data:read",
                "attachments:read", "benefits:read", "consent:read", "consent:write"
            ],
            [EnrollmentSpecialist] =
            [
                "members:read", "members:write",
                "enrollment:read", "enrollment:process",
                "coverage:read", "coverage:write", "reference-data:read",
                // Imports resolve plan codes in benefit-plan-service.
                "benefits:read"
            ],
            [UMCoordinator] =
            [
                "authorizations:read", "authorizations:write", "authorizations:decide",
                "appeals:read", "appeals:write",
                "rfai:read", "rfai:write",
                "correspondence:read", "correspondence:write",
                "members:read", "claims:read",
                "terminology:read", "reference-data:read",
                "attachments:read", "attachments:write", "benefits:read", "consent:read"
            ],
            [ProviderRelations] =
            [
                "providers:read", "providers:write", "providers:credential",
                "contracts:read", "contracts:write",
                "networks:read", "networks:write",
                "terminology:read", "reference-data:read"
            ],
            // Finance prepares payments; it does not approve or release them.
            [Finance] =
            [
                "payments:read", "payments:run",
                "billing:read", "billing:run",
                "finance:read", "finance:write",
                "reports:financial", "claims:read", "contracts:read",
                "trading-partners:read", "risk-adjustment:read",
                "encounters:read"
            ],
            // The checker: approves and releases payments someone else prepared.
            [FinanceApprover] =
            [
                "payments:read", "payments:approve",
                "finance:read", "reports:financial"
            ],
            [ComplianceOfficer] =
            [
                "*:read", "audit:read", "compliance:read", "reports:compliance"
            ],
            [ComplianceViewer] =
            [
                "compliance:read", "authorizations:read", "audit:read"
            ],
            [TenantAdmin] =
            [
                "*:*", "users:manage", "roles:manage",
                "settings:manage", "operating-mode:manage"
            ],
            [PlatformAdmin] =
            [
                "*:*", "users:manage", "roles:manage",
                "settings:manage", "operating-mode:manage",
                "platform:admin", "platform:tenants", "platform:inquiries"
            ],
        };

    /// <summary>All built-in role names.</summary>
    public static IReadOnlyCollection<string> BuiltInRoles => (IReadOnlyCollection<string>)Map.Keys;

    /// <summary>Permissions granted by a built-in role; empty for an unknown role.</summary>
    public static IReadOnlyList<string> ForRole(string role)
        => Map.TryGetValue(role, out var permissions) ? permissions : Array.Empty<string>();

    /// <summary>Flattens a set of role names into the permissions they grant.</summary>
    public static HashSet<string> Expand(IEnumerable<string> roles)
    {
        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles)
            permissions.UnionWith(ForRole(role));
        return permissions;
    }

    /// <summary>
    /// Resources a resource wildcard (<c>*:read</c>, <c>*:*</c>) never reaches.
    /// <c>platform:*</c> permissions act across tenants, so a tenant role's
    /// wildcard must not grant them; they are granted only by name.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedResources =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "platform" };

    /// <summary>
    /// Whether any granted permission satisfies <paramref name="required"/>.
    /// Supports wildcards on either side of the colon: <c>*:read</c>,
    /// <c>claims:*</c> and <c>*:*</c>. A resource wildcard does not match a
    /// <see cref="ReservedResources">reserved resource</see>.
    /// </summary>
    public static bool Satisfies(IEnumerable<string> granted, string required)
    {
        foreach (var permission in granted)
        {
            if (Matches(permission, required))
                return true;
        }
        return false;
    }

    public static bool Matches(string granted, string required)
    {
        if (string.Equals(granted, required, StringComparison.OrdinalIgnoreCase))
            return true;

        var g = granted.Split(':');
        var r = required.Split(':');
        if (g.Length != 2 || r.Length != 2)
            return false;

        var resourceMatch = g[0] == "*"
            ? !ReservedResources.Contains(r[0])
            : string.Equals(g[0], r[0], StringComparison.OrdinalIgnoreCase);
        var actionMatch = g[1] == "*" || string.Equals(g[1], r[1], StringComparison.OrdinalIgnoreCase);
        return resourceMatch && actionMatch;
    }
}
