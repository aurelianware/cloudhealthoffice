using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WorkManagementService.Models;

/// <summary>
/// The kinds of work one engine distributes.
///
/// <para>Most are <b>source-owned</b>: the work exists because a domain
/// service says one of its records needs work, and disappears when it says
/// it does not. <see cref="Inquiry"/> is the first that is not — a service
/// representative creates a follow-up deliberately, and only a person closes
/// it. <see cref="WorkTypes.IsPersonCreated"/> is the boundary, and it is
/// what the create endpoint is allowed to accept.</para>
/// </summary>
public enum WorkType
{
    Claim,
    Appeal,
    PriorAuth,
    Enrollment834,
    Payment,

    /// <summary>
    /// A follow-up from a member or provider contact (cho-service.md §8 step 3).
    /// Person-created: no source service owns it.
    /// </summary>
    Inquiry,
}

/// <summary>Facts about a <see cref="WorkType"/> that are not its name.</summary>
public static class WorkTypes
{
    /// <summary>
    /// Work a person creates and a person closes, with no source service
    /// behind it. Source-owned types are never created through the API:
    /// their items come from their source's events or reconcile, so a forged
    /// create could otherwise put a claim in a queue no claim is pended for.
    /// </summary>
    public static bool IsPersonCreated(WorkType type) => type is WorkType.Inquiry;
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

    /// <summary>Routing criteria. Empty criteria match everything.</summary>
    public WorkQueueCriteria Criteria { get; set; } = new();

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
/// Attributes a queue routes on, for every work type. Every list is "any of";
/// an empty list or a null bound does not constrain. All set constraints must
/// hold.
///
/// <para>The named fields below are the ones nearly every source has — a
/// reason code, a line of business, an amount, a provider, expedited. They
/// are named after claims because claims came first, but none of them is
/// claim-only: an enrollment exception's reason code travels as its pend
/// code, and so does a contact's reason code. What a source has and the
/// named fields do not is a <see cref="Facets">facet</see>.</para>
/// </summary>
public class WorkQueueCriteria
{
    /// <summary>
    /// The source's reason code(s), compared case-insensitively: a claim's
    /// pend code (NCCI, COB, ...), an enrollment exception's reason, a
    /// contact's reason code.
    /// </summary>
    public List<string> PendCodes { get; set; } = new();

    /// <summary>
    /// False (default): <see cref="PendCodes"/> matches the item's primary
    /// (routing) reason code, as the claims work-queue categories always have.
    /// True: any of the item's reason codes matches.
    /// </summary>
    public bool MatchAnyPendCode { get; set; }

    public List<string> LinesOfBusiness { get; set; } = new();

    /// <summary>Claim types; only sources that set <see cref="WorkItemAttributes.ClaimType"/> match these.</summary>
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

    /// <summary>
    /// Attributes the named fields above do not cover, as facet name → the
    /// values that match. A work type adds its own without a schema change
    /// and without every other type growing a field it will never set: a
    /// contact routes on <c>channel</c>, <c>caller</c> and
    /// <c>classification</c>, where a claim has none of the three.
    ///
    /// <para>Facets are "all of, each any of": every named facet must hold,
    /// and within one, any listed value does. A facet with no values does not
    /// constrain, like the lists above. Names are normalised by
    /// <see cref="WorkFacets.Key"/>.</para>
    /// </summary>
    public Dictionary<string, List<string>> Facets { get; set; } = new();
}

/// <summary>The facet names CHO's own sources set, and how a name is normalised.</summary>
public static class WorkFacets
{
    /// <summary>How the contact arrived: phone, chat, email, portal, ...</summary>
    public const string Channel = "channel";

    /// <summary>Who was on the contact: member, representative, provider, other.</summary>
    public const string Caller = "caller";

    /// <summary>A contact's regulatory classification: inquiry, grievance, appeal, ...</summary>
    public const string Classification = "classification";

    /// <summary>
    /// Lower-cased and trimmed. A Mongo document key cannot begin with '$'
    /// or contain '.', so neither can a facet name; <see cref="IsValidKey"/>
    /// is what the admin API refuses on.
    /// </summary>
    public static string Key(string name) => name.Trim().ToLowerInvariant();

    /// <summary>1..40 characters of a-z, 0-9 and underscore.</summary>
    public static bool IsValidKey(string key) =>
        key.Length is > 0 and <= 40 && key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
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

/// <summary>
/// Attribute snapshot of the work's source, taken when the work was needed.
/// The queue criteria of every work type are evaluated against this.
/// </summary>
public class WorkItemAttributes
{
    /// <summary>The routing reason code: a claim's pend code, an exception's reason, a contact's reason.</summary>
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

    /// <summary>
    /// What this item is about, in a sentence, for sources whose record has
    /// no number worth reading on its own ("Call back about the denial on
    /// claim CLM-412"). The queue list shows it where it is set.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Where the portal opens this item, when the work type alone does not
    /// say — a follow-up from a contact opens the contact, not a screen named
    /// after the follow-up. A relative portal path only; see
    /// <see cref="Domain.WorkEntitlements.OpenUrl"/>, which refuses anything
    /// else and falls back to the work type's own screen.
    /// </summary>
    public string? OpenUrl { get; set; }

    /// <summary>
    /// Routing attributes the fields above do not name, as facet name → its
    /// values (a contact's <c>channel</c>, <c>caller</c>, <c>classification</c>).
    /// Matched against <see cref="WorkQueueCriteria.Facets"/>.
    /// </summary>
    public Dictionary<string, List<string>> Facets { get; set; } = new();
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
