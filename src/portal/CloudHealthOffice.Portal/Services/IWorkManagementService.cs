namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// work-management-service (Services:WorkManagementService, ".../api/v1"):
/// My Work (Get Next, leases), supervisor push and queue counts, and the
/// Work Management admin configuration. Transport failures throw
/// <see cref="ServiceUnavailableException"/>; a refusal the service explains
/// (400 / 403 / 404 / 409) throws <see cref="WorkManagementRefusedException"/>.
/// </summary>
public interface IWorkManagementService
{
    /// <summary>Get Next: the caller's own lease, or the next item leased to them; null when there is nothing to work (204).</summary>
    Task<WorkNextResult?> GetNextAsync(string? workType = null);
    Task<List<WorkItemView>> GetMineAsync();
    Task<WorkItemView> GetItemAsync(string workItemId);
    Task<WorkItemView> CompleteAsync(string workItemId, string resolution);
    Task<WorkItemView> ReleaseAsync(string workItemId);
    Task<WorkItemView> RenewAsync(string workItemId);

    Task<List<WorkQueueCounts>> GetQueuesAsync();
    Task<WorkItemPage> ListAsync(string? queueId = null, string? status = null, string? assignedTo = null, int page = 1, int pageSize = 200);
    Task<WorkItemView> AssignAsync(string workItemId, string userId);
    Task<WorkItemView> UnassignAsync(string workItemId);
    Task<List<string>> GetAssigneesAsync();

    // ── Admin (workqueue:admin) ──
    Task<List<WorkQueueConfig>> GetQueueConfigsAsync();
    Task<WorkQueueConfig> SaveQueueAsync(string? queueId, WorkQueueInput input);
    Task DeleteQueueAsync(string queueId);
    Task ReorderQueuesAsync(string workType, IReadOnlyList<string> queueIds);
    Task<List<WorkTeamConfig>> GetTeamsAsync();
    Task<WorkTeamConfig> SaveTeamAsync(string? teamId, string name, IReadOnlyList<string> userIds);
    Task DeleteTeamAsync(string teamId);
    Task<List<WorkMembershipConfig>> GetMembershipsAsync();
    Task<WorkMembershipConfig> SaveMembershipAsync(string? membershipId, WorkMembershipInput input);
    Task DeleteMembershipAsync(string membershipId);
    Task<List<EffectiveWorkQueue>> GetEffectiveQueuesAsync(string userId);
    Task<WorkRecomputeResult> RecomputeAsync();
    Task<WorkReconcileResult> ReconcileAsync();
}

/// <summary>work-management-service refused the request and said why.</summary>
public sealed class WorkManagementRefusedException : Exception
{
    public System.Net.HttpStatusCode StatusCode { get; }

    public WorkManagementRefusedException(System.Net.HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

public sealed class WorkItemAttributesView
{
    public string? PrimaryPendCode { get; set; }
    public List<string> PendCodes { get; set; } = new();
    public string? PendReason { get; set; }
    public string? LineOfBusiness { get; set; }
    public string? ClaimType { get; set; }
    public decimal Amount { get; set; }
    public string? ProviderNpi { get; set; }
    public string? ProviderName { get; set; }
    public bool Expedited { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public string? MemberId { get; set; }
}

public sealed class WorkItemView
{
    public string Id { get; set; } = string.Empty;
    public string WorkType { get; set; } = "Claim";
    public string SourceId { get; set; } = string.Empty;
    public string SourceNumber { get; set; } = string.Empty;
    public string QueueId { get; set; } = string.Empty;
    public string? QueueName { get; set; }
    public double PriorityScore { get; set; }
    public string PriorityReason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? AssignedTo { get; set; }
    public string? AssignedBy { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public bool LeaseActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DueAt { get; set; }
    public string OpenUrl { get; set; } = string.Empty;
    public WorkItemAttributesView Attributes { get; set; } = new();

    public string DisplayNumber => string.IsNullOrWhiteSpace(SourceNumber) ? SourceId : SourceNumber;
}

public sealed class WorkNextResult
{
    public WorkItemView Item { get; set; } = new();
    public bool Resumed { get; set; }
    public string OpenUrl { get; set; } = string.Empty;
}

public sealed class WorkItemPage
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
    public List<WorkItemView> Items { get; set; } = new();
}

public sealed class WorkQueueCounts
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string WorkType { get; set; } = "Claim";
    public bool IsActive { get; set; }
    public bool IsFallback { get; set; }
    public int Order { get; set; }
    public int SlaDays { get; set; }
    public int Open { get; set; }
    public int Leased { get; set; }
}

public sealed class WorkQueueCriteria
{
    public List<string> PendCodes { get; set; } = new();
    public bool MatchAnyPendCode { get; set; }
    public List<string> LinesOfBusiness { get; set; } = new();
    public List<string> ClaimTypes { get; set; } = new();
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public List<string> ProviderNpis { get; set; } = new();
    public List<string> ProviderTins { get; set; } = new();
    public List<string> NetworkTiers { get; set; } = new();
    public bool? Expedited { get; set; }
}

public sealed class WorkPriorityWeights
{
    public double Deadline { get; set; } = 1.0;
    public double Age { get; set; } = 0.5;
    public double Amount { get; set; } = 0.25;
    public double Expedited { get; set; } = 1.0;
}

public sealed class WorkQueueConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string WorkType { get; set; } = "Claim";
    public bool IsActive { get; set; } = true;
    public int Order { get; set; }
    public bool IsFallback { get; set; }
    public WorkQueueCriteria Criteria { get; set; } = new();
    public WorkPriorityWeights Weights { get; set; } = new();
    public int SlaDays { get; set; } = 30;
    public string? SystemKey { get; set; }
}

public sealed class WorkQueueInput
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string WorkType { get; set; } = "Claim";
    public bool IsActive { get; set; } = true;
    public int? Order { get; set; }
    public bool IsFallback { get; set; }
    public WorkQueueCriteria Criteria { get; set; } = new();
    public WorkPriorityWeights Weights { get; set; } = new();
    public int SlaDays { get; set; } = 30;

    public static WorkQueueInput From(WorkQueueConfig q) => new()
    {
        Name = q.Name, Description = q.Description, WorkType = q.WorkType, IsActive = q.IsActive, Order = q.Order,
        IsFallback = q.IsFallback, Criteria = q.Criteria, Weights = q.Weights, SlaDays = q.SlaDays,
    };
}

public sealed class WorkTeamConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> UserIds { get; set; } = new();
}

public sealed class WorkMembershipConfig
{
    public string Id { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? TeamId { get; set; }
    public string QueueId { get; set; } = string.Empty;
    public int Rank { get; set; } = 1;
    public bool IsOverflow { get; set; }
    public decimal? AuthorityLimit { get; set; }
}

public sealed class WorkMembershipInput
{
    public string? UserId { get; set; }
    public string? TeamId { get; set; }
    public string QueueId { get; set; } = string.Empty;
    public int Rank { get; set; } = 1;
    public bool IsOverflow { get; set; }
    public decimal? AuthorityLimit { get; set; }
}

public sealed class EffectiveWorkQueue
{
    public string QueueId { get; set; } = string.Empty;
    public string QueueName { get; set; } = string.Empty;
    public string WorkType { get; set; } = "Claim";
    public int Rank { get; set; }
    public bool IsOverflow { get; set; }
    public decimal? AuthorityLimit { get; set; }
    public List<string> Via { get; set; } = new();
}

public sealed class WorkRecomputeResult
{
    public int Examined { get; set; }
    public int Rerouted { get; set; }
    public int Rescored { get; set; }
    public int Conflicts { get; set; }
}

public sealed class WorkReconcileResult
{
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public int Pended { get; set; }
    public int Created { get; set; }
    public int Reopened { get; set; }
    public int Updated { get; set; }
    public int Closed { get; set; }
}
