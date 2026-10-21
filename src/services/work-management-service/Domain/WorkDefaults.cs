using CloudHealthOffice.Infrastructure.Security;
using WorkManagementService.Models;

namespace WorkManagementService.Domain;

/// <summary>
/// The claim queues a tenant starts with: today's claims-service work-queue
/// categories (ClaimsController.MapPendReason / the summary cards), one queue
/// per category in the same precedence, plus the fallback. Seeded per tenant
/// on first use, idempotently (by <see cref="WorkQueue.SystemKey"/>).
/// </summary>
public static class DefaultClaimQueues
{
    public sealed record Definition(string Key, string Name, string Description, int Order, string[] PendCodes, int SlaDays, bool Fallback = false);

    public static readonly IReadOnlyList<Definition> All =
    [
        new("claim.ncci", "NCCI / MUE Edit Failures", "Claims pended by an NCCI PTP or MUE edit.", 10, ["NCCI", "MUE"], 30),
        new("claim.auth", "Prior Authorization", "Missing authorization, or the prior-authorization lookup was unavailable.", 20, ["AUTH", "NOAUTH", "PAUNAVAIL"], 30),
        new("claim.oon", "Provider Not Contracted", "Out of network / no contract on file.", 30, ["OON", "NOCONTRACT"], 30),
        new("claim.cob", "Coordination of Benefits", "Payer order or other-payer data needs confirming.", 40, ["COB"], 30),
        new("claim.medreview", "Medical Review", "Clinical / medical review required.", 50, ["MEDREVIEW", "CLINICAL"], 30),
        new("claim.svcunavail", "Service Unavailable", "A check could not run (a dependency was down); usually retried, else worked.", 60, ["SVCUNAVAIL", "PLANUNAVAIL"], 30),
        new("claim.duplicate", "Possible Duplicate", "Matches a prior claim; confirm or deny as duplicate.", 70, ["DUPLICATE"], 30),
        new("claim.timely", "Timely Filing", "Received past the filing limit; deny the late lines (CARC 29) or override with proof of timely filing.", 72, ["TIMELY"], 30),
        new("claim.planedit", "Plan Edits", "Pended by the plan's own edit service (ADR 019); uphold or override each edit.", 75, ["PLANEDIT"], 30),
        new("claim.pricing", "Pricing", "Pricing could not be completed.", 80, ["PRICING"], 30),
        new("claim.other", "Other / Pending Review", "Every pended claim no other queue matched.", 1000, [], 30, Fallback: true),
    ];

    public static WorkQueue Create(Definition d, string tenantId, DateTime now) => new()
    {
        Id = $"{tenantId}-{d.Key}".Replace('/', '_'),
        TenantId = tenantId,
        Name = d.Name,
        Description = d.Description,
        WorkType = WorkType.Claim,
        IsActive = true,
        Order = d.Order,
        IsFallback = d.Fallback,
        Criteria = new ClaimQueueCriteria { PendCodes = d.PendCodes.ToList() },
        SlaDays = d.SlaDays,
        SystemKey = d.Key,
        CreatedAt = now,
        UpdatedAt = now,
        UpdatedBy = "system",
    };
}

/// <summary>
/// Entitlement: the role is coarse security over which work TYPES a person
/// may do. A queue membership can only narrow it: Get Next skips queues whose
/// work type the caller's own token does not permit, whatever the membership
/// says. The permission per work type is the one its domain service enforces
/// on the action the work leads to.
/// </summary>
public static class WorkEntitlements
{
    public static string PermissionFor(WorkType type) => type switch
    {
        WorkType.Claim => "claims:work",
        WorkType.Appeal => "appeals:write",
        WorkType.PriorAuth => "authorizations:decide",
        WorkType.Enrollment834 => "enrollment:process",
        WorkType.Payment => "payments:run",
        _ => "platform:admin",
    };

    public static bool Permits(ICurrentActor actor, WorkType type) => actor.HasPermission(PermissionFor(type));

    /// <summary>The screen a work item opens in (portal route).</summary>
    public static string OpenUrl(WorkItem item) => item.WorkType switch
    {
        WorkType.Claim => $"/claims/{Uri.EscapeDataString(item.SourceId)}",
        WorkType.Appeal => $"/appeals?appealId={Uri.EscapeDataString(item.SourceId)}",
        WorkType.PriorAuth => $"/authorizations?authorizationId={Uri.EscapeDataString(item.SourceId)}",
        WorkType.Enrollment834 => "/enrollment-ops",
        WorkType.Payment => "/payment-runs",
        _ => "/my-work",
    };
}
