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
    public const string WorkQueueAssign = "workqueue:assign";

    /// <summary>The Cosmos partition migration reads and writes every tenant's claims.</summary>
    public const string PlatformAdmin = "platform:admin";
}
