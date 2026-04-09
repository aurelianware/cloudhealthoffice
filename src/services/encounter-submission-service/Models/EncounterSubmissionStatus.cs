namespace EncounterSubmissionService.Models;

/// <summary>
/// Lifecycle status of an encounter submission record, from initial creation
/// through FMMIS acknowledgment processing.
/// </summary>
public enum EncounterSubmissionStatus
{
    /// <summary>
    /// Claim adjudicated; awaiting inclusion in next FMMIS batch.
    /// </summary>
    Pending,

    /// <summary>
    /// Included in an FMMIS batch file; awaiting transmission.
    /// </summary>
    Batched,

    /// <summary>
    /// Batch file transmitted to FMMIS; awaiting 999 acknowledgment.
    /// </summary>
    Submitted,

    /// <summary>
    /// FMMIS 999 received — encounter accepted.
    /// </summary>
    Accepted,

    /// <summary>
    /// FMMIS 999 received — partial acceptance (some errors noted).
    /// </summary>
    PartialAccept,

    /// <summary>
    /// FMMIS 999 received — encounter rejected.
    /// </summary>
    Rejected,

    /// <summary>
    /// Approaching the 60-day AHCA submission deadline.
    /// A warning event has been fired for operational escalation.
    /// </summary>
    DeadlineWarning
}
