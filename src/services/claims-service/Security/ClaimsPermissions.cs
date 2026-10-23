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

    /// <summary>
    /// Resolve (approve / deny) a pended claim, and override its edits.
    ///
    /// <para>This is <c>claims:work</c>, not <c>workqueue:work</c>. It was
    /// the latter until 2026-10-23, when member services and provider
    /// relations gained <c>workqueue:work</c> so they could pull the contact
    /// follow-ups they raise — which would otherwise have let them approve
    /// and deny pended claims. Resolving a claim is a claims authority; the
    /// work-queue permission only says a person may use the work engine.</para>
    /// </summary>
    public const string Work = "claims:work";
}
