using AuthorizationService.Models;
using AuthorizationService.Services.Retention;

namespace AuthorizationService.Services;

/// <summary>
/// Receipt-based CHO operational target for medical prior authorizations.
/// These targets do not assert a universal statutory ceiling for every LOB or drug.
/// The clock never pauses or restarts (an RFAI does not stop it). The only way
/// the deadline moves is one lawful extension, taken before the deadline, of at
/// most 14 calendar days, on a line of business whose CMS-0057-F timeframes
/// provide for one: Medicare Advantage (42 CFR 422.568(b)(1)(i), 422.572(b)(1))
/// and Medicaid (42 CFR 438.210(d)(1)(ii), (d)(2)(ii); 440.230(e)(1)). See <see cref="Extend"/>.
/// </summary>
public static class AuthorizationDeadlinePolicy
{
    public const string Version = "cho-receipt-v1";

    public static DateTime DueAt(DateTime receivedAt, string? levelOfService) =>
        Utc(receivedAt).AddHours(IsExpedited(levelOfService) ? 72 : 168);

    public static bool IsExpedited(string? levelOfService) =>
        string.Equals(levelOfService, "U", StringComparison.OrdinalIgnoreCase);

    public static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>The longest extension the CMS-0057-F timeframe provisions allow.</summary>
    public static readonly TimeSpan MaxExtension = TimeSpan.FromDays(14);

    public static void Initialize(Authorization authorization)
    {
        // A new request carries no extension, whatever a create body claimed.
        authorization.DecisionExtension = null;
        authorization.DecisionReceivedAt = Utc(authorization.SubmittedDate);
        authorization.DecisionDueAt = DueAt(authorization.SubmittedDate, authorization.LevelOfService);
        authorization.DecisionDeadlinePolicy = Version;
    }

    public static void InitializeMissing(Authorization authorization)
    {
        authorization.DecisionReceivedAt ??= Utc(authorization.SubmittedDate);
        authorization.DecisionDueAt ??= DueAt(authorization.DecisionReceivedAt.Value, authorization.LevelOfService);
        authorization.DecisionDeadlinePolicy ??= Version;
    }

    public static DateTime Receipt(Authorization authorization) =>
        authorization.DecisionReceivedAt ?? Utc(authorization.SubmittedDate);

    public static DateTime Deadline(Authorization authorization) =>
        authorization.DecisionDueAt ?? DueAt(Receipt(authorization), authorization.LevelOfService);

    /// <summary>The lines of business whose decision timeframes provide for an extension.</summary>
    public static bool AllowsExtension(LineOfBusiness lineOfBusiness) =>
        lineOfBusiness is LineOfBusiness.Medicare or LineOfBusiness.Medicaid;

    /// <summary>
    /// Validates and builds the one lawful extension of an open request's
    /// decision deadline. Throws <see cref="ArgumentException"/> for a malformed
    /// command and <see cref="InvalidOperationException"/> when this request
    /// cannot be extended (decided, wrong line of business, already extended,
    /// or past its deadline). Nothing is changed on <paramref name="authorization"/>.
    /// </summary>
    public static AuthorizationDecisionExtension Extend(
        Authorization authorization, ExtendDecisionDeadline command, string actor, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An authenticated actor is required.");
        if (!Enum.IsDefined(command.Basis) || command.Basis == DecisionExtensionBasis.Unspecified)
            throw new ArgumentException("State why the timeframe is extended.");
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new ArgumentException("An extension needs a written reason the member can be told.");
        if (!authorization.Status.IsOpen())
            throw new InvalidOperationException("Only an undecided request can have its decision timeframe extended.");
        if (!AllowsExtension(authorization.LineOfBusiness))
            throw new InvalidOperationException(
                "Timeframe extensions are modeled only for Medicare Advantage and Medicaid requests.");
        if (authorization.DecisionExtension is not null)
            throw new InvalidOperationException("This request's decision timeframe has already been extended once.");

        var now = Milliseconds(Utc(nowUtc));
        var original = Milliseconds(Deadline(authorization));
        if (now > original)
            throw new InvalidOperationException("An extension must be taken before the decision deadline, not after it.");
        var latest = original + MaxExtension;
        var extended = command.ExtendedDueAt is { } requested ? Milliseconds(requested.UtcDateTime) : latest;
        if (extended <= original || extended > latest)
            throw new ArgumentException("The extended deadline must be later than the current one and at most 14 calendar days after it.");

        return new AuthorizationDecisionExtension
        {
            GrantedAt = now,
            GrantedBy = actor,
            OriginalDueAt = original,
            ExtendedDueAt = extended,
            Basis = command.Basis,
            Reason = command.Reason.Trim(),
        };
    }

    // BSON DateTime is millisecond precision; keep the stored and returned values equal.
    private static DateTime Milliseconds(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
}
