using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WorkManagementService.Models;

/// <summary>
/// The kinds of work one engine distributes. Phase 1 produces Claim only;
/// the others are reserved so queues, memberships and entitlement are typed
/// from day one (phase 2: Appeal, PriorAuth; phase 3: Enrollment834, Payment).
/// </summary>
public enum WorkType
{
    Claim,
    Appeal,
    PriorAuth,
    Enrollment834,
    Payment,
}

public enum WorkItemStatus
{
    /// <summary>Waiting in its queue; any eligible member may pull it.</summary>
    Open,

    /// <summary>Held by <see cref="WorkItem.AssignedTo"/> until <see cref="WorkItem.LeaseExpiresAt"/>.</summary>
    Leased,

    /// <summary>The source no longer needs work (the claim left Pended).</summary>
    Done,

    /// <summary>Withdrawn without being worked (reconcile found no source).</summary>
    Cancelled,
}

/// <summary>
/// A queue: a routing rule on work-item attributes (never on people) plus the
/// priority weights for the items it holds. Each item lands in exactly one
/// queue: the first active queue of its work type, by <see cref="Order"/>,
/// whose criteria match, else that work type's fallback queue.
/// </summary>
public class WorkQueue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    [BsonRepresentation(BsonType.String)]
    public WorkType WorkType { get; set; } = WorkType.Claim;

    public bool IsActive { get; set; } = true;

    /// <summary>Routing order: lower is evaluated first.</summary>
    public int Order { get; set; }

    /// <summary>
    /// Catches every item of its work type no other queue matched. Exactly one
    /// active fallback per work type that has queues; its criteria are ignored.
    /// </summary>
    public bool IsFallback { get; set; }

    /// <summary>Claim routing criteria (Claim queues). Empty criteria match everything.</summary>
    public ClaimQueueCriteria Criteria { get; set; } = new();

    public PriorityWeights Weights { get; set; } = new();

    /// <summary>Days from receipt to the item's due date (prompt-pay / turnaround target).</summary>
    public int SlaDays { get; set; } = 30;

    /// <summary>Set for the seeded default queues; lets a re-seed recognise them.</summary>
    public string? SystemKey { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Claim attributes a queue routes on. Every list is "any of"; an empty list
/// or a null bound does not constrain. All set constraints must hold.
/// </summary>
public class ClaimQueueCriteria
{
    /// <summary>Pend codes (NCCI, COB, ...), compared case-insensitively.</summary>
    public List<string> PendCodes { get; set; } = new();

    /// <summary>
    /// False (default): <see cref="PendCodes"/> matches the claim's routing
    /// (primary) pend code, as the claims work-queue categories always have.
    /// True: any pend code on the claim matches.
    /// </summary>
    public bool MatchAnyPendCode { get; set; }

    public List<string> LinesOfBusiness { get; set; } = new();
    public List<string> ClaimTypes { get; set; } = new();

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal? MinAmount { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal? MaxAmount { get; set; }

    public List<string> ProviderNpis { get; set; } = new();
    public List<string> ProviderTins { get; set; } = new();
    public List<string> NetworkTiers { get; set; } = new();

    /// <summary>Null: either. True / false: only expedited / only non-expedited items.</summary>
    public bool? Expedited { get; set; }
}

/// <summary>
/// Weights of the one priority scale shared by every work type. Each factor
/// is 0..100 points (deadline up to 200 when overdue); the score is the
/// weighted sum. See docs/architecture/work-management.md.
/// </summary>
public class PriorityWeights
{
    public double Deadline { get; set; } = 1.0;
    public double Age { get; set; } = 0.5;
    public double Amount { get; set; } = 0.25;
    public double Expedited { get; set; } = 1.0;
}

/// <summary>A supervisor-managed group; its memberships apply to every member user.</summary>
public class WorkTeam
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> UserIds { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Who pulls from a queue (exactly one of <see cref="UserId"/> /
/// <see cref="TeamId"/>), in what order and up to what amount. Membership
/// is fine-grained and supervisor-managed; it can only ever narrow what the
/// person's role (entitlement) already permits.
/// </summary>
public class WorkMembership
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? TeamId { get; set; }
    public string QueueId { get; set; } = string.Empty;

    /// <summary>Order within a tier: 1 is pulled from first.</summary>
    public int Rank { get; set; } = 1;

    /// <summary>Overflow queues are pulled from only when every primary queue is empty.</summary>
    public bool IsOverflow { get; set; }

    /// <summary>Largest amount (total charge) this member may work in this queue; null = no limit.</summary>
    [BsonRepresentation(BsonType.Decimal128)]
    public decimal? AuthorityLimit { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}

/// <summary>Attribute snapshot of the work's source, taken when the work was needed.</summary>
public class WorkItemAttributes
{
    public string? PrimaryPendCode { get; set; }
    public List<string> PendCodes { get; set; } = new();
    public string? PendReason { get; set; }
    public string? PendFingerprint { get; set; }
    public string? LineOfBusiness { get; set; }
    public string? ClaimType { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Amount { get; set; }

    public string? ProviderNpi { get; set; }
    public string? ProviderTin { get; set; }
    public string? ProviderName { get; set; }
    public string? NetworkTier { get; set; }
    public bool Expedited { get; set; }

    /// <summary>Explicit deadline from the source, when it has one (else the queue SLA applies).</summary>
    public DateTime? Deadline { get; set; }

    public DateTime? ReceivedDate { get; set; }
    public DateTime? ServiceDate { get; set; }
    public string? MemberId { get; set; }
}

/// <summary>
/// One unit of work: one per source (a claim), reopened if the source needs
/// work again. Every write is conditional on <see cref="Version"/>, which is
/// what makes Get Next safe under concurrency: of two callers leasing the
/// same item, one write wins.
/// </summary>
public class WorkItem
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public WorkType WorkType { get; set; } = WorkType.Claim;

    /// <summary>"claim:{claimId}".</summary>
    public string SourceRef { get; set; } = string.Empty;

    /// <summary>The source's own id (claim id), the route key of its domain screen.</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Human-readable number (claim number).</summary>
    public string SourceNumber { get; set; } = string.Empty;

    public WorkItemAttributes Attributes { get; set; } = new();

    public string QueueId { get; set; } = string.Empty;

    public double PriorityScore { get; set; }
    public string PriorityReason { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public WorkItemStatus Status { get; set; } = WorkItemStatus.Open;

    public string? AssignedTo { get; set; }

    /// <summary>The supervisor who pushed it, or the user themself for Get Next.</summary>
    public string? AssignedBy { get; set; }

    public DateTime? LeaseExpiresAt { get; set; }

    /// <summary>
    /// <see cref="LeaseExpiresAt"/> as Unix milliseconds: stores compare a
    /// number, never a serialized date string.
    /// </summary>
    public long? LeaseExpiresAtMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DueAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    public string? Resolution { get; set; }

    /// <summary>Optimistic-concurrency version: +1 on every write.</summary>
    public long Version { get; set; }

    /// <summary>
    /// SourceVersion of the last source event applied. Events at or below it
    /// are duplicates or late, and are ignored.
    /// </summary>
    public long SourceVersion { get; set; }

    public string? LastEventId { get; set; }

    public bool IsActive => Status is WorkItemStatus.Open or WorkItemStatus.Leased;

    /// <summary>Leased and the lease has not run out at <paramref name="now"/>.</summary>
    public bool HasActiveLease(DateTime now) =>
        Status == WorkItemStatus.Leased && LeaseExpiresAt is { } until && until > now;

    public void SetLease(string? user, string? by, DateTime? until)
    {
        AssignedTo = user;
        AssignedBy = by;
        LeaseExpiresAt = until;
        LeaseExpiresAtMs = until is { } u ? new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : null;
    }

    /// <summary>
    /// One item per source per tenant. The tenant is part of the id because a
    /// Mongo _id is unique across the whole collection.
    /// </summary>
    public static string IdFor(string tenantId, WorkType type, string sourceId) =>
        Sanitize($"{tenantId}.{type.ToString().ToLowerInvariant()}-{sourceId}");

    public static string SourceRefFor(WorkType type, string sourceId) =>
        $"{type.ToString().ToLowerInvariant()}:{sourceId}";

    /// <summary>Ids never contain '/', '\\', '?' or '#' (they appear in URL paths).</summary>
    private static string Sanitize(string id) =>
        new(id.Select(c => c is '/' or '\\' or '?' or '#' ? '_' : c).ToArray());
}

public static class WorkAuditActions
{
    public const string Created = "Created";
    public const string Rerouted = "Rerouted";
    public const string Leased = "Leased";
    public const string Reclaimed = "LeaseReclaimed";
    public const string Renewed = "Renewed";
    public const string Released = "Released";
    public const string Assigned = "Assigned";
    public const string Reassigned = "Reassigned";
    public const string Unassigned = "Unassigned";
    public const string Completed = "Completed";
    public const string Reopened = "Reopened";
    public const string Cancelled = "Cancelled";
}

/// <summary>Append-only record of who did what to a work item.</summary>
public class WorkAuditEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string WorkItemId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string? FromUser { get; set; }
    public string? ToUser { get; set; }
    public string? QueueId { get; set; }
    public string? Detail { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Tie-breaker for events with the same <see cref="At"/> (same millisecond, or a
    /// fixed clock): monotonic within a process, assigned when the event is created,
    /// so a work item's history reads in the order it happened.
    /// </summary>
    public long Seq { get; set; } = AuditSequence.Next();
}

/// <summary>Monotonic, time-based sequence for <see cref="WorkAuditEvent.Seq"/>.</summary>
public static class AuditSequence
{
    private static long _last;

    public static long Next()
    {
        while (true)
        {
            var last = Interlocked.Read(ref _last);
            var next = Math.Max(last + 1, DateTime.UtcNow.Ticks);
            if (Interlocked.CompareExchange(ref _last, next, last) == last) return next;
        }
    }
}
