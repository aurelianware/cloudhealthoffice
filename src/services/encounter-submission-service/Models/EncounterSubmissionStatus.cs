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
    /// Final encounter acceptance. Legacy records also used this for a 999;
    /// reconcile those records before treating them as state acceptance.
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
    DeadlineWarning,

    /// <summary>999 syntax acceptance only; final encounter acceptance is not established.</summary>
    SyntaxAccepted,

    /// <summary>999 accepted with errors; final encounter acceptance is not established.</summary>
    SyntaxAcceptedWithErrors,

    /// <summary>
    /// Held back before submission: the encounter failed implementation-guide
    /// validation (missing member address, unbalanced line, and so on). Needs
    /// rework at the source before it can be batched.
    /// </summary>
    ValidationFailed
}
