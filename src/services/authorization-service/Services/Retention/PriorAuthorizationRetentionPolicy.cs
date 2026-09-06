using AuthorizationService.Models;

namespace AuthorizationService.Services.Retention;

/// <summary>
/// Whether an authorization is operationally finished.
///
/// The open/terminal split was previously spelled out separately in the SLA
/// watchdog, the RFAI consumer, and both repositories' queries. Retention is a
/// destructive operation keyed on exactly this distinction, so it is defined
/// once here and reused rather than copied a fifth time.
/// </summary>
public static class AuthorizationStatusExtensions
{
    /// <summary>
    /// Still operational: a decision may yet be made, or information is
    /// outstanding. Never purgeable, whatever the dates say.
    /// </summary>
    public static bool IsOpen(this AuthorizationStatus status) => status switch
    {
        AuthorizationStatus.Submitted => true,
        AuthorizationStatus.InReview => true,
        AuthorizationStatus.Pended => true,
        _ => false,
    };

    /// <summary>
    /// Operationally finished — a decision was reached, the authorization
    /// lapsed, or it was withdrawn. Only these can ever become purge-eligible.
    /// </summary>
    public static bool IsTerminal(this AuthorizationStatus status) => !status.IsOpen();
}

/// <summary>
/// Operational configuration for prior-authorization retention.
///
/// The retention PERIOD is deliberately configurable but floored: CMS-0057-F
/// states a MINIMUM ("retain prior-authorization data for at least one year
/// after the last status change"), not a maximum, and Cloud Health Office
/// retains other regulated records far longer — member documents carry a HIPAA
/// six-year floor and a ten-year product default. Defaulting a destructive job
/// to the regulatory minimum would quietly make CHO's shortest-retaining domain
/// its prior-authorization data, so the default here is six years and anything
/// below the one-year floor is rejected rather than honoured.
/// </summary>
public sealed class PriorAuthorizationRetentionOptions
{
    public const string SectionName = "Cms0057:PriorAuthorizationRetention";

    /// <summary>
    /// The CMS-0057-F minimum. Configuration below this is refused, so a
    /// mis-set value cannot shorten retention past the regulation.
    /// </summary>
    public static readonly TimeSpan RegulatoryFloor = TimeSpan.FromDays(365);

    /// <summary>Master switch. Off by default: a destructive sweep opts IN.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How long a finished authorization is retained after its last status
    /// change. Defaults to six years, matching the HIPAA posture used for member
    /// documents, and never less than <see cref="RegulatoryFloor"/>.
    /// </summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(365 * 6);

    /// <summary>How often the sweep runs. Cadence never affects eligibility.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound on records examined per tenant per sweep.</summary>
    public int MaxRecordsPerTenantPerSweep { get; set; } = 500;

    /// <summary>
    /// Report what would be purged without deleting anything. Mirrors the
    /// dry-run convention the Cosmos claims migration already uses.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>The period actually applied, with the regulatory floor enforced.</summary>
    public TimeSpan EffectiveRetentionPeriod =>
        RetentionPeriod < RegulatoryFloor ? RegulatoryFloor : RetentionPeriod;
}

/// <summary>
/// The retention rule for prior-authorization data, in one place.
///
/// Pure and deterministic: it reads an authorization and a clock and answers
/// three questions. It performs no I/O and deletes nothing, so the rule can be
/// tested exhaustively and the worker that applies it stays a thin sweeper.
/// </summary>
public interface IPriorAuthorizationRetentionPolicy
{
    /// <summary>
    /// Identifies the rule that produced a decision, for the audit trail. Bump
    /// when the rule changes so past purges remain explicable.
    /// </summary>
    string PolicyVersion { get; }

    /// <summary>
    /// The date retention is measured FROM: the last change to the
    /// authorization's lifecycle. Never a read, an inquiry, or an arbitrary
    /// database write timestamp.
    /// </summary>
    DateTime? RetentionAnchorUtc(Authorization authorization);

    /// <summary>
    /// The instant the record stops being retained, or null when no anchor can
    /// be established — in which case the record is kept, not purged.
    /// </summary>
    DateTime? RetentionUntilUtc(Authorization authorization);

    /// <summary>
    /// True only when the authorization is operationally terminal AND past its
    /// retention boundary. Both conditions, always.
    /// </summary>
    bool IsPurgeEligible(Authorization authorization, DateTime asOfUtc);

    /// <summary>
    /// The anchor cutoff for a sweep: records whose anchor is at or before this
    /// are candidates. Lets the query do the coarse filtering in the store while
    /// <see cref="IsPurgeEligible"/> makes the final per-record decision.
    /// </summary>
    DateTime CandidateCutoffUtc(DateTime asOfUtc);
}

/// <inheritdoc />
public sealed class PriorAuthorizationRetentionPolicy : IPriorAuthorizationRetentionPolicy
{
    private readonly PriorAuthorizationRetentionOptions _options;

    public PriorAuthorizationRetentionPolicy(
        Microsoft.Extensions.Options.IOptions<PriorAuthorizationRetentionOptions> options)
        => _options = options.Value;

    /// <summary>
    /// v1: anchor on the last status change; retain for the configured period,
    /// floored at the CMS-0057-F minimum; never purge a non-terminal record.
    /// </summary>
    public string PolicyVersion => "pa-retention-v1";

    public DateTime? RetentionAnchorUtc(Authorization authorization)
    {
        if (authorization is null)
            return null;

        // The lifecycle history is the authoritative record of status changes.
        var lastStatusChange = authorization.StatusHistory?
            .Where(h => h is not null)
            .Select(h => (DateTime?)h.ChangedAt)
            .Max();

        if (lastStatusChange.HasValue)
            return DateTime.SpecifyKind(lastStatusChange.Value, DateTimeKind.Utc);

        // Records written outside the CHO-native backend can carry an empty
        // history. Fall back to the decision date, then to submission — both are
        // stable lifecycle facts. Deliberately NOT LastUpdatedDate: every write
        // touches it, so an unrelated edit would silently move the boundary.
        var fallback = authorization.ReviewedDate ?? authorization.SubmittedDate;
        return fallback == default
            ? null
            : DateTime.SpecifyKind(fallback, DateTimeKind.Utc);
    }

    public DateTime? RetentionUntilUtc(Authorization authorization)
    {
        var anchor = RetentionAnchorUtc(authorization);
        return anchor?.Add(_options.EffectiveRetentionPeriod);
    }

    public bool IsPurgeEligible(Authorization authorization, DateTime asOfUtc)
    {
        if (authorization is null)
            return false;

        // An open authorization is never purgeable, however old its timestamps.
        if (authorization.Status.IsOpen())
            return false;

        var retainUntil = RetentionUntilUtc(authorization);

        // No anchor means no defensible boundary. Keep the record.
        if (!retainUntil.HasValue)
            return false;

        return asOfUtc >= retainUntil.Value;
    }

    public DateTime CandidateCutoffUtc(DateTime asOfUtc)
        => asOfUtc - _options.EffectiveRetentionPeriod;
}
