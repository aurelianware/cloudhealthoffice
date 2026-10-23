using ContactService.Models;

namespace ContactService.Services;

/// <summary>
/// The rules a contact obeys, kept out of the controller so they can be read
/// and tested on their own.
///
/// Step 1 is deliberately permissive: it records what happened rather than
/// policing it, because a rep on a call must never be blocked by the record.
/// The one thing it does insist on is that a closed contact stops changing,
/// since a contact is evidence of a conversation. Mandatory classification
/// arrives in step 2 of cho-service.md §8 and belongs here when it does.
/// </summary>
public static class ContactLifecycle
{
    /// <summary>
    /// How far ahead of the server clock a caller-supplied start time may be
    /// before it is treated as wrong. A small allowance absorbs clock skew
    /// between CHO and a phone platform handing over a call.
    /// </summary>
    public static readonly TimeSpan StartSkewAllowance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Why a contact may not be changed, or null when it may. A closed
    /// contact is reopened explicitly, not edited back into life.
    /// </summary>
    public static string? WhyNotEditable(Contact contact) =>
        contact.Status == ContactStatus.Closed
            ? "This contact is closed. Reopen it before changing it."
            : null;

    /// <summary>
    /// Why a contact may not be closed with these details, or null when it
    /// may.
    /// </summary>
    public static string? WhyNotCloseable(Contact contact, DateTime endedAt)
    {
        if (contact.Status == ContactStatus.Closed) return "This contact is already closed.";
        if (endedAt < contact.StartedAt) return "A contact cannot end before it started.";
        return null;
    }

    /// <summary>
    /// Why a start time is not usable, or null when it is. A contact
    /// recorded after the fact is normal; one that starts in the future is a
    /// mistake, and it would make handle time negative.
    /// </summary>
    public static string? WhyNotStartable(DateTime startedAt, DateTime now) =>
        startedAt > now + StartSkewAllowance
            ? "A contact cannot start in the future."
            : null;

    /// <summary>
    /// Talk time in whole seconds, never negative. Null while the contact is
    /// still open.
    /// </summary>
    public static int? Duration(DateTime startedAt, DateTime? endedAt) =>
        endedAt is { } ended ? (int)Math.Max(0, Math.Round((ended - startedAt).TotalSeconds)) : null;

    /// <summary>
    /// The contact number used when a plan does not supply its own:
    /// <c>C-20261022-A1B2C3D4</c>. Readable over the phone, sortable by day,
    /// and unique enough that two reps starting a call in the same second do
    /// not collide (the unique index is the real guarantee).
    /// </summary>
    public static string NextContactNumber(DateTime now) =>
        $"C-{now:yyyyMMdd}-{Guid.NewGuid().ToString("n")[..8].ToUpperInvariant()}";

    /// <summary>
    /// Keeps the reason codes a tenant actually has, upper-cased and
    /// de-duplicated, and reports the rest. An unknown code is dropped rather
    /// than rejected: a rep mid-call should not be stopped because the
    /// taxonomy changed under them, and the dropped codes are returned so the
    /// screen can say so.
    /// </summary>
    public static (List<string> Accepted, List<string> Unknown) SiftReasonCodes(
        IEnumerable<string> requested, IReadOnlySet<string> known)
    {
        var accepted = new List<string>();
        var unknown = new List<string>();

        foreach (var raw in requested)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var code = raw.Trim().ToUpperInvariant();
            if (accepted.Contains(code) || unknown.Contains(code)) continue;

            (known.Contains(code) ? accepted : unknown).Add(code);
        }

        return (accepted, unknown);
    }

    /// <summary>Appends one entry to the contact's append-only history.</summary>
    public static void Record(Contact contact, string action, string by, DateTime at, string? detail = null)
    {
        contact.History.Add(new ContactRevisionEntry
        {
            Action = action,
            Detail = detail,
            At = at,
            By = by,
        });
        contact.UpdatedAt = at;
        contact.UpdatedBy = by;
    }

    public static class Actions
    {
        public const string Started = "Started";
        public const string Updated = "Updated";
        public const string SubjectLinked = "SubjectLinked";
        public const string Classified = "Classified";
        public const string Closed = "Closed";
        public const string Reopened = "Reopened";
    }
}
