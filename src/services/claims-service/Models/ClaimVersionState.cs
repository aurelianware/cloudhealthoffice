using System.Text.Json.Serialization;

namespace ClaimsService.Models;

/// <summary>
/// Lifecycle state of a single <see cref="Claim"/> version document.
///
/// <para>State machine (see docs/architecture/claim-versioning.md):</para>
/// <list type="bullet">
///   <item><c>Draft</c> — mutable; not yet submitted. Non-terminal /
///         <c>UpdateAsync</c>-writable.</item>
///   <item><c>Submitted</c> — claim submitted, awaiting adjudication.
///         Non-terminal / <c>UpdateAsync</c>-writable. Encompasses the
///         operational <c>ClaimStatus</c> sub-states <c>Submitted</c>,
///         <c>Received</c>, <c>InAdjudication</c>, and <c>Pended</c>: those
///         are transient pipeline-stage outcomes within the same
///         version-state.</item>
///   <item><c>Adjudicated</c> — adjudication complete (approved or denied);
///         awaits payment if approved. Non-terminal /
///         <c>UpdateAsync</c>-writable. The <c>ClaimStatus.Approved</c>
///         operational sub-state lives here.</item>
///   <item><c>Paid</c> — payment processed (terminal — <c>UpdateAsync</c>
///         throws <see cref="Exceptions.ClaimVersionStateException"/>). The
///         <c>ClaimStatus.PartiallyPaid</c> operational sub-state lives here
///         too.</item>
///   <item><c>Denied</c> — denied with no payment (terminal).</item>
///   <item><c>Adjusted</c> — superseded by an adjustment version (terminal
///         from this row's perspective; <c>SupersededByVersionId</c> points
///         at the replacement).</item>
///   <item><c>Voided</c> — claim voided / reversed (terminal).</item>
/// </list>
///
/// <c>UpdateAsync</c> accepts writes against any non-terminal state
/// (<c>Draft</c>, <c>Submitted</c>, <c>Adjudicated</c>) and throws
/// <see cref="Exceptions.ClaimVersionStateException"/> against terminal
/// states. Adjustments out of a terminal state must go through the
/// explicit "create new version with PredecessorVersionId" path
/// (capability 5.12), not <c>UpdateAsync</c>.
///
/// Documents persisted before this field existed hydrate to <c>Submitted</c>,
/// <c>Adjudicated</c>, <c>Paid</c>, <c>Denied</c>, or <c>Voided</c> based on
/// their <see cref="ClaimStatus"/> value (see <c>ClaimRepository.Hydrate</c>).
///
/// PR #705 enum convention: <c>Unknown=0</c> as the default state for
/// uninitialized / legacy records; <c>JsonStringEnumConverter</c> for stable
/// wire format.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClaimVersionState
{
    /// <summary>Default for uninitialized / legacy records (PR #705 convention).</summary>
    Unknown = 0,
    Draft = 1,
    Submitted = 2,
    Adjudicated = 3,
    Paid = 4,
    Denied = 5,
    Adjusted = 6,
    Voided = 7
}
