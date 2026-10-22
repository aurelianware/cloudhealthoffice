using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace CareManagementService.Models;

/// <summary>
/// Something the care manager has to do, by a date.
///
/// The activity lives on the case because it is part of the care record. The
/// work queue that surfaces it lives in work-management-service, which
/// already routes, assigns and ages work for the rest of the platform — so an
/// activity is pushed there rather than given a second queue of its own.
/// <see cref="WorkItemId"/> is the handle that comes back.
/// </summary>
[BsonIgnoreExtraElements]
public class CareActivity
{
    [Required]
    [StringLength(50)]
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];

    [Required]
    [StringLength(300)]
    public string Description { get; set; } = string.Empty;

    /// <summary>What kind of contact or task: call, letter, home visit, referral, follow-up, assessment.</summary>
    [StringLength(50)]
    public string? ActivityType { get; set; }

    /// <summary>The intervention this activity carries out, where it belongs to one.</summary>
    [StringLength(50)]
    public string? InterventionId { get; set; }

    public DateTime? DueDate { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CareActivityStatus Status { get; set; } = CareActivityStatus.Open;

    [StringLength(200)]
    public string? AssignedTo { get; set; }

    public DateTime? CompletedAt { get; set; }

    [StringLength(1000)]
    public string? Outcome { get; set; }

    /// <summary>
    /// Handle returned by work-management-service. Null when the work queue
    /// was unreachable: the activity is still recorded on the case, and
    /// nothing about the care record depends on the queue being up.
    /// </summary>
    [StringLength(100)]
    public string? WorkItemId { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedBy { get; set; }
}

public enum CareActivityStatus
{
    Open = 1,
    Completed = 2,

    /// <summary>No longer needed — the case closed, or the member's situation changed.</summary>
    Cancelled = 3
}
