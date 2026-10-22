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

    /// <summary>
    /// Sets and releases a legal hold on a record (member documents today).
    /// It is a write: <c>*:read</c> never satisfies it, and neither does a
    /// resource's own write permission such as <c>members:write</c>.
    /// </summary>
    public const string LegalHold = "records:legal-hold";

    /// <summary>
    /// Work management (work-management-service). The role is coarse
    /// entitlement; which queues a person pulls from is queue membership,
    /// managed by a supervisor. <c>workqueue:read</c> sees queues and items,
    /// <c>workqueue:work</c> pulls (Get Next), renews and releases one's own
    /// leases, <c>workqueue:assign</c> pushes work (assign / reassign /
    /// unassign), <c>workqueue:admin</c> configures queues, teams and
    /// memberships and runs recompute / reconcile. TenantAdmin holds all four
    /// through <c>*:*</c>.
    /// </summary>
    public const string WorkQueueRead = "workqueue:read";

    /// <inheritdoc cref="WorkQueueRead"/>
    public const string WorkQueueWork = "workqueue:work";

    /// <inheritdoc cref="WorkQueueRead"/>
    public const string WorkQueueAssign = "workqueue:assign";

    /// <inheritdoc cref="WorkQueueRead"/>
    public const string WorkQueueAdmin = "workqueue:admin";

    /// <summary>
    /// The user's own workspace on Home: read plan notices (and mark them
    /// read) and pin records to follow. Every staff role holds it; it opens
    /// no record data by itself (a pinned record is still read through its
    /// own service and permission).
    /// </summary>
    public const string WorkspaceUse = "workspace:use";

    /// <summary>
    /// Outbound letters (correspondence-service). Reading a letter also needs
    /// the read permission of one of its subjects; requesting one needs read
    /// access to every subject.
    /// </summary>
    public const string CorrespondenceRead = "correspondence:read";

    /// <inheritdoc cref="CorrespondenceRead"/>
    public const string CorrespondenceWrite = "correspondence:write";

    /// <summary>Author letter types and template drafts, and preview them. TenantAdmin holds it through <c>*:*</c>.</summary>
    public const string CorrespondenceTemplates = "correspondence:templates";

    /// <summary>
    /// Approve and retire template versions: the wording members receive.
    /// Approval is by someone other than the version's author.
    /// </summary>
    public const string CorrespondenceApprove = "correspondence:approve";

    /// <summary>Post, edit and withdraw plan notices. TenantAdmin holds it through <c>*:*</c>.</summary>
    public const string NoticesWrite = "notices:write";

    /// <summary>
    /// Contacts: the record of a call, chat or walk-in (contact-service).
    /// <c>service:read</c> reads contacts and the tenant's reason taxonomy;
    /// <c>service:contact</c> starts one, works it and closes it.
    ///
    /// Reading a contact does not open what it discussed: a linked claim is
    /// still read through claims-service and <c>claims:read</c>. Compliance
    /// reads contacts through <c>*:read</c>, which is how grievance audits
    /// reach the calls that opened the cases.
    /// </summary>
    public const string ServiceRead = "service:read";

    /// <inheritdoc cref="ServiceRead"/>
    public const string ServiceContact = "service:contact";

    /// <summary>
    /// Configures the tenant's contact reason taxonomy: the list every
    /// service report is counted by. A supervisor's job, not a rep's.
    /// TenantAdmin holds it through <c>*:*</c>.
    /// </summary>
    public const string ServiceAdmin = "service:admin";

    /// <summary>
    /// The document catalog (document-service). Reading a document also needs
    /// the read permission of the subject it is opened from (a claim's
    /// documents need <c>claims:read</c>), and the document must be linked to
    /// that subject. <c>documents:write</c> uploads and links.
    /// </summary>
    public const string DocumentsRead = "documents:read";

    /// <inheritdoc cref="DocumentsRead"/>
    public const string DocumentsWrite = "documents:write";

    /// <summary>
    /// Opens documents carrying a sensitivity tag (behavioral health, HIV,
    /// reproductive, genetic, minor-confidential). Exact-only
    /// (<see cref="ExactOnlyPermissions"/>): no wildcard grants it, TenantAdmin
    /// included. 42 CFR Part 2 records additionally need a consent check and
    /// stay withheld until that check exists.
    /// </summary>
    public const string DocumentsReadSensitive = "documents:read-sensitive";

    private static readonly string[] ExaminerPermissions =
    [
        "claims:read", "claims:work", "claims:override-request", "claims:note",
        "workqueue:read", "workqueue:work", WorkspaceUse,
        "members:read", "accumulators:read", "providers:read",
        "terminology:read", "reference-data:read",
        "attachments:read", "attachments:write", "benefits:read",
        DocumentsRead, DocumentsWrite
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Map =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [ClaimsExaminer] = ExaminerPermissions,
            [ClaimsSupervisor] =
            [
                .. ExaminerPermissions,
                "claims:override-approve", "claims:override-edit", "workqueue:assign", "workqueue:reassign",
                // Supervisors manage their teams' queue memberships and the
                // queue configuration (work-management-service).
                "workqueue:admin",
                "reports:claims", "claims:void", "claims:adjust", "trading-partners:read",
                "encounters:read", "encounters:write"
            ],
            [MemberServices] =
            [
                WorkspaceUse,
                "members:read", "members:search", "accumulators:read",
                "eligibility:check", "claims:read", "claims:note", "coverage:read",
                "authorizations:read", "reference-data:read",
                "attachments:read", "benefits:read", "consent:read", "consent:write",
                "providers:read",
                // The call itself: member services is the role that lives in
                // the contact screen.
                ServiceRead, ServiceContact,
                // Member correspondence and forms taken on calls.
                DocumentsRead, DocumentsWrite,
                // Members ask for a Payer-to-Payer transfer through member services.
                "payer-to-payer:initiate"
            ],
            [EnrollmentSpecialist] =
            [
                WorkspaceUse,
                "members:read", "members:write",
                "enrollment:read", "enrollment:process",
                "coverage:read", "coverage:write", "reference-data:read",
                // Imports resolve plan codes in benefit-plan-service and
                // providers by NPI in provider-service.
                "benefits:read", "providers:read",
                // Member documents: enrollment forms, proof of coverage, ID cards.
                DocumentsRead, DocumentsWrite,
                // Coverage transitions are enrollment work; a member's request
                // for a Payer-to-Payer transfer can also arrive here.
                "payer-to-payer:initiate"
            ],
            [UMCoordinator] =
            [
                WorkspaceUse,
                "authorizations:read", "authorizations:write", "authorizations:decide",
                "appeals:read", "appeals:write",
                "rfai:read", "rfai:write",
                CorrespondenceRead, CorrespondenceWrite,
                "members:read", "claims:read",
                "terminology:read", "reference-data:read",
                "attachments:read", "attachments:write", "benefits:read", "consent:read",
                // Clinical review reads records the sensitive tags cover
                // (behavioral health in particular).
                DocumentsRead, DocumentsWrite, DocumentsReadSensitive,
                // USCDI clinical data (Condition, Observation, MedicationRequest...)
                // for clinical review. Not granted to MemberServices.
                "clinical:read"
            ],
            [ProviderRelations] =
            [
                WorkspaceUse,
                // Claim status, denials and payments for provider calls, and
                // the call recorded on the claim. Never claims:work.
                "claims:read", "claims:note",
                // Provider calls are contacts too, recorded the same way.
                ServiceRead, ServiceContact,
                "providers:read", "providers:write", "providers:credential",
                "contracts:read", "contracts:write",
                "networks:read", "networks:write",
                "terminology:read", "reference-data:read"
            ],
            // Finance prepares payments; it does not approve or release them.
            [Finance] =
            [
                WorkspaceUse,
                "payments:read", "payments:run",
                "billing:read", "billing:run",
                "finance:read", "finance:write",
                "reports:financial", "claims:read", "contracts:read",
                "trading-partners:read",
                // Finance calculates RAF scores and runs the CMS submissions.
                "risk-adjustment:read", "risk-adjustment:write",
                "encounters:read",
                // Code-set lookups (not PHI).
                "reference-data:read"
            ],
            // The checker: approves and releases payments someone else prepared.
            // billing:read lets it review the premium invoices and billing runs
            // behind the sponsor debits it releases; it still cannot run billing.
            [FinanceApprover] =
            [
                WorkspaceUse,
                "payments:read", "payments:approve",
                "billing:read",
                "finance:read", "reports:financial",
                "reference-data:read"
            ],
            // Compliance owns legal holds: a held record cannot be deleted or
            // changed until compliance releases it, with a reason. TenantAdmin
            // and PlatformAdmin hold records:legal-hold through *:*.
            [ComplianceOfficer] =
            [
                WorkspaceUse,
                "*:read", "audit:read", "compliance:read", "reports:compliance",
                LegalHold,
                // Compliance signs off the wording of member notices.
                CorrespondenceApprove,
                // Investigations and HIPAA accounting reach sensitive records;
                // *:read alone does not.
                DocumentsReadSensitive
            ],
            [ComplianceViewer] =
            [
                WorkspaceUse,
                "compliance:read", "authorizations:read", "audit:read",
                "reference-data:read"
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
    /// Permissions only an exact grant satisfies: no wildcard (<c>*:read</c>,
    /// <c>*:*</c>, <c>documents:*</c>) reaches them, and a service token does
    /// not hold them by default. Each opens records a broad role should not
    /// see without being given them by name: sensitive documents (behavioral
    /// health, HIV, reproductive, genetic, minor-confidential) and call
    /// records, which have no defined access yet.
    /// </summary>
    public static readonly IReadOnlySet<string> ExactOnlyPermissions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DocumentsReadSensitive, "calls:read" };

    /// <summary>Whether <paramref name="permission"/> is satisfied only by an exact grant (<see cref="ExactOnlyPermissions"/>).</summary>
    public static bool IsExactOnly(string permission) => ExactOnlyPermissions.Contains(permission.Trim());

    /// <summary>Whether <paramref name="permission"/> names a <see cref="ReservedResources">reserved resource</see>.</summary>
    public static bool IsReserved(string permission)
    {
        var colon = permission.IndexOf(':');
        var resource = colon < 0 ? permission : permission[..colon];
        return ReservedResources.Contains(resource.Trim());
    }

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
        if (IsExactOnly(required))
            return false;

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
