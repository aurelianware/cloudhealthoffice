using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace CareManagementService.Models;

/// <summary>
/// A completed assessment: the questions a programme asks a member, and what
/// they answered.
///
/// The template is named rather than embedded. Instruments differ per plan,
/// per state programme and per vendor, and several are licensed, so what is
/// stored is the answers, the template that produced them and its version —
/// never a copy of someone else's instrument.
/// </summary>
[BsonIgnoreExtraElements]
public class CareAssessment
{
    [Required]
    [StringLength(50)]
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];

    /// <summary>Template that produced these answers, e.g. "health-risk-assessment", "phq-9".</summary>
    [Required]
    [StringLength(100)]
    public string TemplateId { get; set; } = string.Empty;

    [StringLength(200)]
    public string? TemplateName { get; set; }

    /// <summary>
    /// Template version. Recorded because a score only means something
    /// alongside the version of the instrument that produced it.
    /// </summary>
    [StringLength(50)]
    public string? TemplateVersion { get; set; }

    public List<CareAssessmentAnswer> Answers { get; set; } = new();

    /// <summary>Total score, where the instrument produces one.</summary>
    public decimal? Score { get; set; }

    /// <summary>What the score means in the instrument's own banding, e.g. "moderate".</summary>
    [StringLength(100)]
    public string? ScoreInterpretation { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CareAssessmentStatus Status { get; set; } = CareAssessmentStatus.InProgress;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    [StringLength(200)]
    public string? CompletedBy { get; set; }

    /// <summary>When this assessment should be repeated, where the programme repeats it.</summary>
    public DateTime? NextDueDate { get; set; }
}

/// <summary>One answer. Values are stored as given; the instrument owns their meaning.</summary>
[BsonIgnoreExtraElements]
public class CareAssessmentAnswer
{
    [Required]
    [StringLength(100)]
    public string QuestionId { get; set; } = string.Empty;

    [StringLength(500)]
    public string? QuestionText { get; set; }

    [StringLength(2000)]
    public string? Value { get; set; }

    /// <summary>Points this answer contributed, where the instrument scores.</summary>
    public decimal? Points { get; set; }
}

public enum CareAssessmentStatus
{
    InProgress = 1,
    Completed = 2,

    /// <summary>Started and abandoned — the member did not finish it.</summary>
    Abandoned = 3
}
