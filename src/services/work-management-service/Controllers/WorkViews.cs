using WorkManagementService.Domain;
using WorkManagementService.Models;

namespace WorkManagementService.Controllers;

/// <summary>A work item as the API returns it: queue name, open URL, no store internals.</summary>
public sealed class WorkItemView
{
    public string Id { get; set; } = string.Empty;
    public WorkType WorkType { get; set; }
    public string SourceRef { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string SourceNumber { get; set; } = string.Empty;
    public string QueueId { get; set; } = string.Empty;
    public string? QueueName { get; set; }
    public double PriorityScore { get; set; }
    public string PriorityReason { get; set; } = string.Empty;
    public WorkItemStatus Status { get; set; }
    public string? AssignedTo { get; set; }
    public string? AssignedBy { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public bool LeaseActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Resolution { get; set; }
    public long Version { get; set; }
    public WorkItemAttributes Attributes { get; set; } = new();

    /// <summary>Where the portal opens the item (its domain screen, e.g. /claims/{id}).</summary>
    public string OpenUrl { get; set; } = string.Empty;

    public static WorkItemView From(WorkItem item, IReadOnlyDictionary<string, WorkQueue> queues, DateTime now) => new()
    {
        Id = item.Id,
        WorkType = item.WorkType,
        SourceRef = item.SourceRef,
        SourceId = item.SourceId,
        SourceNumber = item.SourceNumber,
        QueueId = item.QueueId,
        QueueName = queues.TryGetValue(item.QueueId, out var q) ? q.Name : null,
        PriorityScore = item.PriorityScore,
        PriorityReason = item.PriorityReason,
        Status = item.Status,
        AssignedTo = item.AssignedTo,
        AssignedBy = item.AssignedBy,
        LeaseExpiresAt = item.LeaseExpiresAt,
        LeaseActive = item.HasActiveLease(now),
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        DueAt = item.DueAt,
        CompletedAt = item.CompletedAt,
        Resolution = item.Resolution,
        Version = item.Version,
        Attributes = item.Attributes,
        OpenUrl = WorkEntitlements.OpenUrl(item),
    };
}

/// <summary>The Get Next answer.</summary>
public sealed class GetNextResponse
{
    public WorkItemView Item { get; set; } = new();

    /// <summary>True when this is the caller's own lease, returned before pulling anything new.</summary>
    public bool Resumed { get; set; }

    public string OpenUrl { get; set; } = string.Empty;
}

public sealed class QueueSummaryView
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public WorkType WorkType { get; set; }
    public bool IsActive { get; set; }
    public bool IsFallback { get; set; }
    public int Order { get; set; }
    public int SlaDays { get; set; }
    public int Open { get; set; }
    public int Leased { get; set; }
}

public sealed class WorkItemPage
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
    public List<WorkItemView> Items { get; set; } = new();
}

public sealed class WorkItemDetail
{
    public WorkItemView Item { get; set; } = new();
    public List<WorkAuditEvent> Audit { get; set; } = new();
}

public sealed class AssignRequest
{
    public string UserId { get; set; } = string.Empty;
}

/// <summary>
/// Work a person raises: today a follow-up from a contact. The source id is
/// not here — the service generates it, so two follow-ups from one contact
/// cannot overwrite each other.
/// </summary>
public sealed class CreateWorkRequest
{
    public WorkType WorkType { get; set; } = WorkType.Inquiry;

    /// <summary>What needs doing, in a sentence; shown in the queue and on My Work.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The source's human-readable number (the contact number).</summary>
    public string? SourceNumber { get; set; }

    /// <summary>Relative portal path the item opens at (the contact it came from).</summary>
    public string? OpenUrl { get; set; }

    /// <summary>Reason codes to route on; the first is the routing (primary) one.</summary>
    public List<string>? ReasonCodes { get; set; }

    /// <summary>Routing attributes the named fields do not cover (channel, caller, classification).</summary>
    public Dictionary<string, List<string>>? Facets { get; set; }

    public string? MemberId { get; set; }
    public string? ProviderNpi { get; set; }
    public string? LineOfBusiness { get; set; }
    public bool Expedited { get; set; }

    /// <summary>An explicit deadline; without one the queue's SLA sets the due date.</summary>
    public DateTime? DueAt { get; set; }
}

public sealed class CompleteRequest
{
    public string? Resolution { get; set; }
}

public sealed class ReorderRequest
{
    public WorkType WorkType { get; set; } = WorkType.Claim;
    public List<string> QueueIds { get; set; } = new();
}

public sealed class EffectiveQueueView
{
    public string QueueId { get; set; } = string.Empty;
    public string QueueName { get; set; } = string.Empty;
    public WorkType WorkType { get; set; }
    public int Rank { get; set; }
    public bool IsOverflow { get; set; }
    public decimal? AuthorityLimit { get; set; }
    public List<string> Via { get; set; } = new();
}
