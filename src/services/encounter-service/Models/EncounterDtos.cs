using System.ComponentModel.DataAnnotations;

namespace EncounterService.Models;

/// <summary>
/// Request to update encounter status (e.g., from 999/277CA acknowledgment).
/// </summary>
public class EncounterStatusUpdate
{
    [Required]
    public EncounterStatus Status { get; set; }

    [StringLength(5)]
    public string? Edi999Status { get; set; }

    public List<EncounterRejectionReason>? RejectionReasons { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

/// <summary>
/// Request to create a batch of encounters for dispatch.
/// </summary>
public class BatchDispatchRequest
{
    [Required]
    [StringLength(50)]
    public string PayerId { get; set; } = string.Empty;

    public LineOfBusiness? LineOfBusiness { get; set; }

    public EncounterType? EncounterType { get; set; }

    /// <summary>
    /// Maximum number of encounters to include in this batch.
    /// Defaults to 1000 per X12 best practices.
    /// </summary>
    [Range(1, 5000)]
    public int MaxBatchSize { get; set; } = 1000;
}

/// <summary>
/// Result of a batch dispatch operation.
/// </summary>
public class BatchDispatchResult
{
    public string BatchId { get; set; } = string.Empty;
    public string PayerId { get; set; } = string.Empty;
    public int EncounterCount { get; set; }
    public DateTime DispatchedDate { get; set; }
    public List<string> EncounterIds { get; set; } = new();
}

/// <summary>
/// Request to submit a correction (void + replace) for an existing encounter.
/// </summary>
public class CorrectionRequest
{
    /// <summary>
    /// The corrected encounter data (replaces the original).
    /// </summary>
    [Required]
    public Encounter CorrectedEncounter { get; set; } = new();

    /// <summary>
    /// Reason for the correction.
    /// </summary>
    [Required]
    [StringLength(1000)]
    public string CorrectionReason { get; set; } = string.Empty;
}

/// <summary>
/// Result of a correction operation — contains both the void and the replacement.
/// </summary>
public class CorrectionResult
{
    public Encounter VoidEncounter { get; set; } = new();
    public Encounter ReplacementEncounter { get; set; } = new();
}

/// <summary>
/// Summary statistics for encounters.
/// </summary>
public class EncounterSummary
{
    public int TotalEncounters { get; set; }
    public int PendingEncounters { get; set; }
    public int QueuedEncounters { get; set; }
    public int SubmittedEncounters { get; set; }
    public int AcceptedEncounters { get; set; }
    public int RejectedEncounters { get; set; }
    public int CorrectionEncounters { get; set; }
    public decimal TotalChargeAmount { get; set; }
    public decimal AcceptanceRate { get; set; }
}
