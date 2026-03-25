using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ArService.Models;

/// <summary>
/// AR Adjustment — write-offs, retroactive enrollment corrections, grace period adjustments.
/// QNXT analog: AR Adjustment.
/// </summary>
public class ArAdjustment
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(50)]
    public string AdjustmentNumber { get; set; } = string.Empty;

    [Required]
    public ArAdjustmentType AdjustmentType { get; set; }

    [Required]
    public string GlAccountId { get; set; } = string.Empty;

    [Required]
    public string ArBalanceId { get; set; } = string.Empty;

    [Required]
    public DateTime Period { get; set; }

    [Required]
    public decimal Amount { get; set; }

    public ArAdjustmentDirection Direction { get; set; }

    [Required]
    [StringLength(100)]
    public string ReasonCode { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Narrative { get; set; }

    [StringLength(200)]
    public string? AuthorizedBy { get; set; }

    public DateTime? AuthorizedAt { get; set; }

    public string? SourceType { get; set; }
    public string? SourceReferenceId { get; set; }

    [Required]
    public ArAdjustmentStatus Status { get; set; } = ArAdjustmentStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedBy { get; set; }
}

public enum ArAdjustmentType
{
    WriteOff = 1, WriteBack = 2, GracePeriodExtension = 3,
    RetroEnrollment = 4, RetroTermination = 5, ManualCorrection = 6, InterFund = 7
}

public enum ArAdjustmentDirection { Debit = 1, Credit = 2 }

public enum ArAdjustmentStatus { Pending = 1, Approved = 2, Posted = 3, Reversed = 4, Rejected = 5 }
