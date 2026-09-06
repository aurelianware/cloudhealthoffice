using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// What a payer decided about one requested item, normalized just far enough to
/// be compared across two operations.
///
/// Deliberately coarse. It exists so that "what <c>$submit</c> said" and "what
/// <c>$inquire</c> said" can be compared as states rather than as strings, and
/// so a scenario can ask one question the raw code cannot answer — "is this a
/// state the payer may advance on its own?". It is not a re-statement of X12
/// semantics, and nothing asserts equality on it alone: the raw review action
/// code is what continuity is asserted on, and the raw code is what evidence
/// carries.
/// </summary>
public enum PasDisposition
{
    /// <summary>No review action code was present at all.</summary>
    Absent,

    /// <summary>A code outside the set the pinned implementations emit. Reported, never guessed at.</summary>
    Unknown,

    /// <summary>A1 — certified.</summary>
    Approved,

    /// <summary>A2 — not certified.</summary>
    Denied,

    /// <summary>A3 — no authorization required for this item.</summary>
    NotRequired,

    /// <summary>A4 — pended. The payer has not finished deciding.</summary>
    Pended,

    /// <summary>A6 — certified with modifications.</summary>
    Modified,

    /// <summary>C — cancelled.</summary>
    Cancelled,
}

/// <summary>
/// One item's review action, as the payer expressed it, plus the normalized
/// <see cref="PasDisposition"/>.
///
/// The raw <see cref="Code"/>, <see cref="System"/> and <see cref="Display"/> are
/// preserved exactly as received. A scenario asserts continuity on the code and
/// reports the disposition, so a payer that changes only its display wording
/// between two operations does not fail a test, and a payer that changes the code
/// does.
/// </summary>
public sealed record PasReviewAction(
    int? ItemSequence,
    string? System,
    string? Code,
    string? Display)
{
    /// <summary>
    /// The normalized state.
    ///
    /// The X12 005010/306 code list PAS binds to is licensed and is not
    /// redistributed inside the IG package, so this table maps only the codes the
    /// pinned Da Vinci implementations actually emit — A1, A2, A3, A4, A6 and C,
    /// with the meanings those implementations give them. Anything else is
    /// <see cref="PasDisposition.Unknown"/>: an unrecognized code is an
    /// observation to record, never a state to invent.
    /// </summary>
    public PasDisposition Disposition => Code switch
    {
        null or "" => PasDisposition.Absent,
        "A1" => PasDisposition.Approved,
        "A2" => PasDisposition.Denied,
        "A3" => PasDisposition.NotRequired,
        "A4" => PasDisposition.Pended,
        "A6" => PasDisposition.Modified,
        "C" => PasDisposition.Cancelled,
        _ => PasDisposition.Unknown,
    };

    /// <summary>
    /// True when the payer may move this item on its own, without another
    /// request. A pended item is the case that matters: the pinned br-payer
    /// schedules its own resolution of a pend, so asserting that a pended state
    /// is still pended some seconds later would be asserting a race.
    /// </summary>
    public bool IsSelfAdvancing => Disposition == PasDisposition.Pended;

    /// <summary>PHI-free: an item sequence and a code, nothing about the member or the service.</summary>
    public string SafeSummary() =>
        $"item {ItemSequence?.ToString() ?? "?"}: {Code ?? "(no code)"}" +
        (Display is null ? "" : $" ({Display})") +
        $" [{Disposition}]";
}

/// <summary>
/// Reads review actions off a PAS ClaimResponse.
///
/// PAS puts the decision on <c>ClaimResponse.item.adjudication</c>, inside the
/// <c>reviewAction</c> extension, rather than anywhere a generic FHIR reader
/// would look — <c>ClaimResponse.outcome</c> is <c>complete</c> for an approval,
/// a denial and a pend alike, because it describes whether processing finished,
/// not what was decided. A scenario that read <c>outcome</c> would see no
/// difference between the three.
/// </summary>
public static class PasReviewStatus
{
    /// <summary>Every item's review action, in the order the response carries them.</summary>
    public static IReadOnlyList<PasReviewAction> From(ClaimResponse? claimResponse)
    {
        if (claimResponse is null)
        {
            return Array.Empty<PasReviewAction>();
        }

        var actions = new List<PasReviewAction>();

        foreach (var item in claimResponse.Item)
        {
            var coding = item.Adjudication
                .SelectMany(adjudication => adjudication.Extension)
                .Where(extension => extension.Url == PasProtocol.ReviewActionExtension)
                .SelectMany(reviewAction => reviewAction.Extension)
                .Where(extension => extension.Url == PasProtocol.ReviewActionCodeExtension)
                .Select(extension => extension.Value)
                .OfType<CodeableConcept>()
                .SelectMany(concept => concept.Coding)
                .FirstOrDefault();

            // An item with adjudication but no reviewAction is recorded with a
            // null code rather than skipped: "the payer adjudicated this item and
            // said nothing about the review" is a finding, and dropping the item
            // would hide it.
            actions.Add(new PasReviewAction(
                item.ItemSequence, coding?.System, coding?.Code, coding?.Display));
        }

        return actions;
    }

    /// <summary>The review action for one item sequence, or null when the response has no such item.</summary>
    public static PasReviewAction? ForItem(IReadOnlyList<PasReviewAction> actions, int? itemSequence) =>
        actions.FirstOrDefault(action => action.ItemSequence == itemSequence);

    /// <summary>
    /// True when the two sets describe the same decision: the same item
    /// sequences, each carrying the same review action code.
    ///
    /// Compared on code rather than on display text, because display wording is
    /// the payer's prose and may legitimately differ between two operations that
    /// report the same state.
    /// </summary>
    public static bool SameDecision(
        IReadOnlyList<PasReviewAction> left,
        IReadOnlyList<PasReviewAction> right)
    {
        // Keyed on the item sequence rendered as text: an item with no sequence is
        // still an item the payer answered, and dropping it would let a response
        // that lost one compare equal to one that did not.
        static Dictionary<string, string?> ByItem(IReadOnlyList<PasReviewAction> actions) =>
            actions.ToDictionary(
                action => action.ItemSequence?.ToString() ?? "(none)",
                action => action.Code,
                StringComparer.Ordinal);

        var leftByItem = ByItem(left);
        var rightByItem = ByItem(right);

        return leftByItem.Count == rightByItem.Count
               && leftByItem.All(entry =>
                   rightByItem.TryGetValue(entry.Key, out var code)
                   && string.Equals(entry.Value, code, StringComparison.Ordinal));
    }

    /// <summary>PHI-free summary of a decision set, for evidence and assertion messages.</summary>
    public static string SafeSummary(IReadOnlyList<PasReviewAction> actions) =>
        actions.Count == 0
            ? "(no adjudicated items)"
            : string.Join("; ", actions.Select(action => action.SafeSummary()));
}
