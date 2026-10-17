namespace ClaimsService;

/// <summary>
/// Permissions claims-service enforces beyond its defaults (claims:read for
/// reads, claims:work for writes). Names match
/// <see cref="CloudHealthOffice.Infrastructure.Security.ChoRolePermissions"/>.
/// </summary>
public static class ClaimsPermissions
{
    public const string Void = "claims:void";
    public const string Adjust = "claims:adjust";
    public const string OverrideApprove = "claims:override-approve";

    /// <summary>Override an edit whose policy level is Supervisor (claim-review-workbench §2.6).</summary>
    public const string OverrideEdit = "claims:override-edit";

    /// <summary>Add a note to a claim: claims staff and member services.</summary>
    public const string Note = "claims:note";

    /// <summary>Read the claim access log (compliance roles).</summary>
    public const string AuditRead = "audit:read";
    public const string WorkQueueAssign = "workqueue:assign";

    /// <summary>Work the examiner queue: resolve (approve / deny) a pended claim.</summary>
    public const string WorkQueueWork = "workqueue:work";
}
