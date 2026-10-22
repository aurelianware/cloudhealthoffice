using System.ComponentModel.DataAnnotations;

namespace CareManagementService.Models;

/// <summary>A new case, as it arrives over HTTP.</summary>
public sealed class OpenCareCaseRequest
{
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    public string? MemberFirstName { get; set; }
    public string? MemberLastName { get; set; }

    [Required]
    public CareProgram Program { get; set; }

    public CareAcuity Acuity { get; set; } = CareAcuity.Unknown;
    public CareReferralSource ReferralSource { get; set; } = CareReferralSource.Unknown;

    /// <summary>The authorization, stay or claim that referred the case, where something did.</summary>
    public string? ReferralReference { get; set; }

    public string? ReferralReason { get; set; }
    public string? CareManagerId { get; set; }
    public string? CareManagerName { get; set; }
    public DateTime? NextContactDueDate { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// Case number. Generated when the caller does not supply one, so a
    /// referral from another service never has to invent one.
    /// </summary>
    [StringLength(50)]
    public string? CaseNumber { get; set; }
}

/// <summary>A status change asked for over HTTP.</summary>
public sealed class CareCaseStatusRequest
{
    [Required]
    public CareCaseStatus Status { get; set; }

    public string? Reason { get; set; }

    /// <summary>Required when closing.</summary>
    public CareCaseClosureOutcome? ClosureOutcome { get; set; }
}

/// <summary>Progress recorded against one goal.</summary>
public sealed class GoalProgressRequest
{
    [Required]
    public CareGoalStatus Status { get; set; }

    [Required]
    [StringLength(1000)]
    public string Note { get; set; } = string.Empty;
}

/// <summary>A case as the caseload list shows it, without the whole record.</summary>
public sealed record CareCaseSummary
{
    public required string Id                  { get; init; }
    public required string CaseNumber          { get; init; }
    public required string MemberId            { get; init; }
    public string? MemberName                  { get; init; }
    public required CareProgram Program        { get; init; }
    public required CareCaseStatus Status      { get; init; }
    public required CareAcuity Acuity          { get; init; }
    public string? CareManagerName             { get; init; }
    public DateTime? NextContactDueDate        { get; init; }

    /// <summary>True when the next contact date has passed.</summary>
    public required bool ContactOverdue        { get; init; }

    public required int OpenActivities         { get; init; }
    public CarePlanStatus? CarePlanStatus      { get; init; }
    public required int TotalGoals             { get; init; }
    public required int AchievedGoals          { get; init; }
    public required int OverdueGoals           { get; init; }
    public required int OpenBarriers           { get; init; }
}
