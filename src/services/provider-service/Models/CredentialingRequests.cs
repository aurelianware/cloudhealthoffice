using System.ComponentModel.DataAnnotations;
using ProviderService.Models.CredentialingPayloads;

namespace ProviderService.Models;

/// <summary>
/// Body for <c>POST /api/v1/providers/{id}/credentialing/applications</c>.
/// Opens a new credentialing chain.
/// </summary>
public sealed class SubmitApplicationRequest
{
    /// <summary>When the application was submitted. Defaults to <c>UtcNow</c> when null.</summary>
    public DateTimeOffset? SubmittedAt { get; set; }

    /// <summary>Free-form source identifier (e.g. <c>"CAQH"</c>, <c>"Manual"</c>, <c>"DelegatedRoster"</c>).</summary>
    [Required]
    [StringLength(100)]
    public string ApplicationSource { get; set; } = string.Empty;

    /// <summary>Optional supporting documents (license, board cert, malpractice insurance).</summary>
    public IReadOnlyList<DocumentReference>? SupportingDocuments { get; set; }
}

/// <summary>
/// Body for <c>POST /api/v1/providers/{id}/credentialing/verifications</c>.
/// Records primary-source verification completion against the open chain.
/// </summary>
public sealed class RecordPrimarySourceVerificationRequest
{
    public DateTimeOffset? VerifiedAt { get; set; }

    [Required]
    [StringLength(100)]
    public string VerificationVendor { get; set; } = string.Empty;

    [Required]
    public IReadOnlyList<string> VerifiedItems { get; set; } = Array.Empty<string>();

    public IReadOnlyList<DocumentReference>? Evidence { get; set; }
}

/// <summary>
/// Body for <c>POST /api/v1/providers/{id}/credentialing/committee-reviews</c>.
/// Schedules a committee review for the open chain.
/// </summary>
public sealed class ScheduleCommitteeReviewRequest
{
    [Required]
    public DateTimeOffset ScheduledFor { get; set; }

    [Required]
    [StringLength(100)]
    public string CommitteeId { get; set; } = string.Empty;

    [StringLength(500)]
    public string? AgendaReference { get; set; }
}

/// <summary>
/// Body for <c>POST /api/v1/providers/{id}/credentialing/decisions</c> and
/// for the rewired legacy <c>PUT /providers/{id}/credentialing</c> endpoint
/// (translated from <see cref="CredentialingUpdateRequest"/>).
/// </summary>
public sealed class RecordDecisionRequest
{
    [Required]
    public CredentialingDecision Decision { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTime? CredentialingDate { get; set; }

    public DateTime? RecredentialingDueDate { get; set; }

    [Required]
    public DecisionAuthorityType DecisionAuthorityType { get; set; }

    [Required]
    [StringLength(200)]
    public string DecisionAuthorityId { get; set; } = string.Empty;

    public IReadOnlyList<string>? CommitteeMembers { get; set; }

    [StringLength(500)]
    public string? DecisionMinuteReference { get; set; }

    [StringLength(2000)]
    public string? DenialReason { get; set; }
}

/// <summary>
/// Body for <c>POST /api/v1/providers/{id}/credentialing/recredential</c>.
/// Opens a new chain linked to the predecessor approval.
/// </summary>
public sealed class TriggerRecredentialingRequest
{
    public DateTimeOffset? TriggeredAt { get; set; }

    [Required]
    [StringLength(200)]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Body for
/// <c>POST /api/v1/providers/{id}/credentialing/applications/{eventId}/withdraw</c>.
/// </summary>
public sealed class WithdrawApplicationRequest
{
    public DateTimeOffset? WithdrawnAt { get; set; }

    [Required]
    [StringLength(500)]
    public string Reason { get; set; } = string.Empty;
}
