using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace CareManagementService.Models;

/// <summary>
/// The member's care plan: what is wrong, what better looks like, and what
/// anyone is doing about it.
///
/// Problems, goals and interventions are kept as separate lists joined by id
/// rather than nested, because a single intervention commonly serves several
/// goals and a goal commonly spans several problems. Nesting them would force
/// a choice that the clinical reality does not have.
/// </summary>
[BsonIgnoreExtraElements]
public class CarePlan
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CarePlanStatus Status { get; set; } = CarePlanStatus.Draft;

    /// <summary>When the plan was activated with the member. Null while it is a draft.</summary>
    public DateTime? ActivatedDate { get; set; }

    /// <summary>When the plan should next be reviewed with the member.</summary>
    public DateTime? NextReviewDate { get; set; }

    public List<CareProblem> Problems { get; set; } = new();
    public List<CareGoal> Goals { get; set; } = new();
    public List<CareIntervention> Interventions { get; set; } = new();

    /// <summary>
    /// What is standing in the way: transport, housing, cost, language,
    /// health literacy. Recorded because a plan whose goals keep slipping
    /// usually has a barrier nobody wrote down.
    /// </summary>
    public List<CareBarrier> Barriers { get; set; } = new();

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }
}

/// <summary>A problem the plan addresses.</summary>
[BsonIgnoreExtraElements]
public class CareProblem
{
    [Required]
    [StringLength(50)]
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];

    [Required]
    [StringLength(300)]
    public string Description { get; set; } = string.Empty;

    /// <summary>ICD-10 code where the problem is a diagnosis. Null for a social or functional problem.</summary>
    [StringLength(10)]
    public string? DiagnosisCode { get; set; }

    public DateTime IdentifiedDate { get; set; } = DateTime.UtcNow;

    public bool Resolved { get; set; }
    public DateTime? ResolvedDate { get; set; }
}

/// <summary>What better looks like, in terms the member and the care manager both recognise.</summary>
[BsonIgnoreExtraElements]
public class CareGoal
{
    [Required]
    [StringLength(50)]
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];

    [Required]
    [StringLength(300)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Ids of the problems this goal addresses.</summary>
    public List<string> ProblemIds { get; set; } = new();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CareGoalStatus Status { get; set; } = CareGoalStatus.Proposed;

    public DateTime? TargetDate { get; set; }
    public DateTime? AchievedDate { get; set; }

    /// <summary>
    /// How the goal is measured, in the member's own terms where possible:
    /// "walks to the mailbox without resting", "HbA1c below 8".
    /// </summary>
    [StringLength(300)]
    public string? Measure { get; set; }

    /// <summary>Dated progress notes against this goal, newest last.</summary>
    public List<CareGoalProgress> Progress { get; set; } = new();
}

/// <summary>One dated note on how a goal is going.</summary>
[BsonIgnoreExtraElements]
public class CareGoalProgress
{
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CareGoalStatus Status { get; set; }

    [Required]
    [StringLength(1000)]
    public string Note { get; set; } = string.Empty;

    [StringLength(200)]
    public string? RecordedBy { get; set; }
}

/// <summary>Something somebody is doing about a goal.</summary>
[BsonIgnoreExtraElements]
public class CareIntervention
{
    [Required]
    [StringLength(50)]
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];

    [Required]
    [StringLength(300)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Ids of the goals this intervention serves.</summary>
    public List<string> GoalIds { get; set; } = new();

    /// <summary>Who is doing it: the care manager, the member, a provider, a caregiver.</summary>
    [StringLength(100)]
    public string? ResponsibleParty { get; set; }

    /// <summary>How often, where the intervention repeats: "weekly call", "monthly".</summary>
    [StringLength(100)]
    public string? Frequency { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    public bool Completed { get; set; }
}

/// <summary>Something standing in the way of the plan.</summary>
[BsonIgnoreExtraElements]
public class CareBarrier
{
    [Required]
    [StringLength(50)]
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];

    [Required]
    [StringLength(300)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Kind of barrier: transportation, housing, financial, language, literacy, caregiver, behavioral.</summary>
    [StringLength(50)]
    public string? Category { get; set; }

    public DateTime IdentifiedDate { get; set; } = DateTime.UtcNow;

    public bool Resolved { get; set; }
    public DateTime? ResolvedDate { get; set; }

    [StringLength(500)]
    public string? Resolution { get; set; }
}

public enum CarePlanStatus
{
    /// <summary>Being written. Not yet agreed with the member.</summary>
    Draft = 1,

    /// <summary>Agreed with the member and in effect.</summary>
    Active = 2,

    /// <summary>Paused with the case.</summary>
    OnHold = 3,

    /// <summary>Finished, whether or not the goals were met.</summary>
    Completed = 4,

    /// <summary>Abandoned — the plan was superseded or the member withdrew.</summary>
    Cancelled = 5
}

/// <summary>
/// Goal states, matching the FHIR Goal achievement statuses the plan is
/// projected into, so the projection does not have to invent a mapping.
/// </summary>
public enum CareGoalStatus
{
    /// <summary>Written down, not yet agreed with the member.</summary>
    Proposed = 1,

    /// <summary>Agreed and being worked toward.</summary>
    InProgress = 2,

    /// <summary>Met.</summary>
    Achieved = 3,

    /// <summary>Worked toward but not met, and no longer being pursued.</summary>
    NotAchieved = 4,

    /// <summary>No longer relevant — the problem resolved or the member's situation changed.</summary>
    Cancelled = 5
}
