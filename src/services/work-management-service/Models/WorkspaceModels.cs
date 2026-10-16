using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WorkManagementService.Models;

/// <summary>How a plan notice is shown on Home.</summary>
public enum NoticeSeverity
{
    Info,

    /// <summary>Shown first and highlighted (an outage, a deadline).</summary>
    Important,
}

/// <summary>
/// A bulletin an administrator posts for the plan's staff ("new fee schedule
/// effective 11/1", "maintenance Saturday"), shown on Home between
/// <see cref="StartsAt"/> and <see cref="ExpiresAt"/> to the roles it
/// targets (all staff when <see cref="TargetRoles"/> is empty).
/// </summary>
[BsonIgnoreExtraElements]
public class PlanNotice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public NoticeSeverity Severity { get; set; } = NoticeSeverity.Info;

    public DateTime StartsAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public List<string> TargetRoles { get; set; } = new();

    /// <summary>Taken down early by an administrator; kept for the record.</summary>
    public bool Withdrawn { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public const int MaxTitle = 120;
    public const int MaxBody = 2000;

    /// <summary>Shown now to someone holding <paramref name="roles"/>.</summary>
    public bool IsVisibleTo(IReadOnlyCollection<string> roles, DateTime now) =>
        !Withdrawn
        && StartsAt <= now
        && (ExpiresAt is null || ExpiresAt > now)
        && (TargetRoles.Count == 0 || TargetRoles.Any(t => roles.Contains(t, StringComparer.OrdinalIgnoreCase)));
}

/// <summary>That a user marked a notice read. Id: <c>{noticeId}|{userId}</c>.</summary>
[BsonIgnoreExtraElements]
public class NoticeRead
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string NoticeId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTime ReadAt { get; set; }

    public static string KeyFor(string noticeId, string userId) => $"{noticeId}|{userId}";
}

/// <summary>
/// A record a user follows from Home (a member in case management, an open
/// grievance). Private to the user. Id: <c>{userId}|{kind}|{recordId}</c>.
/// </summary>
[BsonIgnoreExtraElements]
public class PinnedRecord
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    /// <summary>Member, Claim, Provider, Authorization, Appeal, Payment.</summary>
    public string Kind { get; set; } = string.Empty;

    public string RecordId { get; set; } = string.Empty;

    /// <summary>What Home shows ("Ana Reyes", "Claim CLM-77"); set by the portal, never PHI beyond what the record header shows.</summary>
    public string Label { get; set; } = string.Empty;

    public DateTime PinnedAt { get; set; }

    public static readonly IReadOnlySet<string> Kinds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Member", "Claim", "Provider", "Authorization", "Appeal", "Payment" };

    public const int MaxPerUser = 50;

    public static string KeyFor(string userId, string kind, string recordId) => $"{userId}|{kind}|{recordId}";
}
